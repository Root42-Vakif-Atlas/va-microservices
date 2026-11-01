"""Vakıf Atlas moderasyon servisi (FastAPI, yalnızca iç ağ; ADR 58).

    POST /v1/moderate   bir anının metinlerini (zararlılık, spam, eserle bağlam) ve fotoğraflarını skorlar
                        (Memories servisi çağırır)
    POST /v1/assess-report  Sivil Koruma hasar bildiriminin kanıt sinyalleri (aynı skorlar + hasar tarifine
                        benzerlik, tür uyumu, katalog kopyası; CivilProtection çağırır, etiketi kendisi verir)
    POST /v1/classify   yalnızca metinleri skorlar
    GET  /v1/model      model kimlikleri, kategoriler, referans ve bağlam indeksleri
    GET  /health/live   süreç ayakta
    GET  /health/ready  modeller yüklendi ve ısındı

Metin ve görsel hiçbir zaman loglanmaz; yalnızca öğe sayısı ve süre ölçülür.
"""

from __future__ import annotations

import asyncio
import logging
import os
import time
from collections.abc import AsyncIterator, Callable
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse

from app import telemetry
from app.engine import Classification, ModerationEngine
from app.schemas import (
    AssessReportRequest,
    AssessReportResponse,
    AssessTextResult,
    ClassifyRequest,
    ClassifyResponse,
    ClassifyResult,
    ContextInfoResponse,
    DamageResult,
    ImageResult,
    LexiconHit,
    ModelInfoResponse,
    ModerateRequest,
    ModerateResponse,
    ReferencesInfoResponse,
    ServiceInfoResponse,
    VisionInfoResponse,
)

log = logging.getLogger("vakifatlas.moderation")


def _default_engine() -> ModerationEngine:
    from app.context import ContextIndex, OnnxTextEmbedder
    from app.language import LanguageDetector
    from app.model import OnnxToxicityModel
    from app.references import ReferenceIndex
    from app.vision import OnnxVisionModels

    threads = int(os.environ.get("ORT_THREADS", "1"))
    model_dir = Path(os.environ.get("MODEL_DIR", "/app/models"))
    model = OnnxToxicityModel(model_dir, threads=threads)
    model.warm_up()
    vision = OnnxVisionModels(model_dir, threads=threads)
    vision.warm_up()
    # İndeks yoksa servis çalışır, benzerlik null döner (Memories benzerlik kuralını uygulamaz).
    references_path = Path(os.environ.get("REFERENCES_PATH", model_dir / "references.npz"))
    references = ReferenceIndex.load(references_path) if references_path.is_file() else None
    if references is None:
        log.warning("reference index %s not found; monument similarity disabled", references_path)
    # Bağlam modeli veya indeksi yoksa relevance null döner (Memories bağlam kuralını uygulamaz).
    embedder, context = None, None
    context_path = Path(os.environ.get("CONTEXT_PATH", model_dir / "context.npz"))
    if (model_dir / "context.json").is_file() and context_path.is_file():
        embedder = OnnxTextEmbedder(model_dir, threads=threads)
        embedder.warm_up()
        context = ContextIndex.load(context_path)
    else:
        log.warning("context model or index %s not found; text relevance disabled", context_path)
    return ModerationEngine(model, LanguageDetector(), vision, references, embedder, context)


def _model(engine: ModerationEngine) -> ModelInfoResponse:
    info = engine.info
    return ModelInfoResponse(name=info.name, version=info.version, categories=list(info.categories))


def _vision(engine: ModerationEngine) -> VisionInfoResponse | None:
    if engine.vision is None:
        return None
    info = engine.vision.info
    return VisionInfoResponse(
        name=info.name, version=info.version, nsfw=info.nsfw_model, embedding=info.embedding_model
    )


def _references(engine: ModerationEngine) -> ReferencesInfoResponse | None:
    if engine.references is None:
        return None
    info = engine.references.info
    return ReferencesInfoResponse(version=info.version, works=info.works, images=info.images)


def _context(engine: ModerationEngine) -> ContextInfoResponse | None:
    if engine.embedder is None or engine.context is None:
        return None
    model, index = engine.embedder.info, engine.context.info
    return ContextInfoResponse(
        name=model.name,
        version=model.version,
        embedding=model.embedding_model,
        index=index.version,
        works=index.works,
        prototypes=index.prototypes,
        damage_prototypes=index.damage_prototypes,
    )


def _results(ids: list[str], results: list[Classification]) -> list[ClassifyResult]:
    return [
        ClassifyResult(
            id=id_,
            scores=result.scores,
            lexicon=[LexiconHit(category=m.category, severity=m.severity) for m in result.lexicon],
            language=result.language.code,
            language_supported=result.language.supported,
            spam=result.spam.score,
            spam_signals=result.spam.signals,
            relevance=result.relevance.score if result.relevance else None,
            monument_relevance=result.relevance.monument if result.relevance else None,
        )
        for id_, result in zip(ids, results, strict=True)
    ]


def _images(items, results) -> list[ImageResult]:
    return [
        ImageResult(
            id=item.id,
            readable=result.readable,
            nsfw=result.nsfw,
            similarity=result.similarity,
            references=result.references,
            catalog_copy=result.catalog_copy,
        )
        for item, result in zip(items, results, strict=True)
    ]


def create_app(engine_factory: Callable[[], ModerationEngine] = _default_engine) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        started = time.perf_counter()
        app.state.engine = await run_in_threadpool(engine_factory)
        # ORT oturumu eşzamanlı çalışabilir; çekirdek sayısını aşan istek sırada bekler.
        app.state.slots = asyncio.Semaphore(int(os.environ.get("MAX_CONCURRENCY", "2")))
        log.info("model %s ready in %.0f ms", app.state.engine.info.version, (time.perf_counter() - started) * 1000)
        yield

    app = FastAPI(title="Vakıf Atlas Moderation", version="1.2.0", lifespan=lifespan, docs_url=None, redoc_url=None)
    telemetry.instrument(app)

    @app.get("/health/live")
    async def live() -> dict[str, str]:
        return {"status": "Healthy"}

    @app.get("/health/ready")
    async def ready(request: Request) -> JSONResponse:
        ok = getattr(request.app.state, "engine", None) is not None
        return JSONResponse({"status": "Healthy" if ok else "Unhealthy"}, status_code=200 if ok else 503)

    @app.get("/v1/model", response_model=ServiceInfoResponse, response_model_by_alias=True)
    async def model_info(request: Request) -> ServiceInfoResponse:
        engine: ModerationEngine = request.app.state.engine
        return ServiceInfoResponse(
            **_model(engine).model_dump(),
            vision=_vision(engine),
            references=_references(engine),
            context=_context(engine),
        )

    @app.post("/v1/classify", response_model=ClassifyResponse, response_model_by_alias=True)
    async def classify(body: ClassifyRequest, request: Request) -> ClassifyResponse:
        engine: ModerationEngine = request.app.state.engine
        started = time.perf_counter()
        async with request.app.state.slots:
            results = await run_in_threadpool(engine.classify, [item.text for item in body.items], body.monument_id)
        telemetry.record_inference(time.perf_counter() - started, len(body.items))
        return ClassifyResponse(
            model=_model(engine), context=_context(engine), results=_results([item.id for item in body.items], results)
        )

    @app.post("/v1/moderate", response_model=ModerateResponse, response_model_by_alias=True)
    async def moderate(body: ModerateRequest, request: Request) -> ModerateResponse | JSONResponse:
        engine: ModerationEngine = request.app.state.engine
        if body.images and engine.vision is None:
            return JSONResponse({"detail": "vision models are not loaded"}, status_code=503)

        def run():
            texts = engine.classify([item.text for item in body.texts], body.monument_id) if body.texts else []
            images = [item.data for item in body.images]
            return texts, engine.classify_images(images, body.monument_id) if images else []

        started = time.perf_counter()
        async with request.app.state.slots:
            texts, images = await run_in_threadpool(run)
        telemetry.record_inference(time.perf_counter() - started, len(body.texts), len(body.images))
        return ModerateResponse(
            model=_model(engine),
            vision=_vision(engine),
            references=_references(engine),
            context=_context(engine),
            texts=_results([item.id for item in body.texts], texts),
            images=_images(body.images, images),
        )

    @app.post("/v1/assess-report", response_model=AssessReportResponse, response_model_by_alias=True)
    async def assess_report(body: AssessReportRequest, request: Request) -> AssessReportResponse | JSONResponse:
        engine: ModerationEngine = request.app.state.engine
        if body.images and engine.vision is None:
            return JSONResponse({"detail": "vision models are not loaded"}, status_code=503)

        def run():
            texts = engine.classify([item.text for item in body.texts], body.monument_id, damage=True)
            images = [item.data for item in body.images]
            return texts, engine.classify_images(images, body.monument_id) if images else []

        started = time.perf_counter()
        async with request.app.state.slots:
            texts, images = await run_in_threadpool(run)
        telemetry.record_inference(time.perf_counter() - started, len(body.texts), len(body.images))
        return AssessReportResponse(
            model=_model(engine),
            vision=_vision(engine),
            references=_references(engine),
            context=_context(engine),
            texts=[
                AssessTextResult(
                    **base.model_dump(),
                    damage=None
                    if result.damage is None
                    else DamageResult(relevance=result.damage.relevance, categories=result.damage.categories),
                )
                for base, result in zip(_results([item.id for item in body.texts], texts), texts, strict=True)
            ],
            images=_images(body.images, images),
        )

    return app


app = create_app()
