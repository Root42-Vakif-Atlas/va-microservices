# syntax=docker/dockerfile:1.7
# Vakıf Atlas moderasyon servisi (ADR 58): metin (Detoxify, spam kuralları, MiniLM eser bağlamı) + fotoğraf (NSFW,
# DINOv2 eser benzerliği).
# Bağlam: bu klasör; ek bağlam "seed": VGM verisi (eser referans ve bağlam indeksleri için).
#   docker build --build-context seed=seed -t vakif-atlas/moderation src/Services/Moderation
#
# export: Detoxify checkpoint'i (SHA-256 sabit) → ONNX → INT8; görsel ve bağlam modelleri sabit Hugging Face
#         revizyonlarından ONNX'e (NSFW ve bağlam INT8); seed'den referans (görsel) ve bağlam (metin) indeksleri;
#         birim + altın testler gerçek modellerle koşar.
#         ORT kuantizasyonu ~7 GB bellek ister (CI runner'ı 16 GB). Docker belleği küçükse modeller host'ta
#         üretilir ve kopyalanır (README → Geliştirme):
#           docker build --build-arg MODEL_SOURCE=local -t vakif-atlas/moderation src/Services/Moderation
# runtime: yalnızca onnxruntime + tokenizers + Pillow (torch yok), root olmayan kullanıcı (UID 1654), port 8080.

ARG PYTHON_VERSION=3.13
ARG MODEL_SOURCE=export

FROM python:${PYTHON_VERSION}-slim AS export
WORKDIR /build
COPY pyproject.toml ./
RUN --mount=type=cache,id=pip,target=/root/.cache/pip \
    python -c "import tomllib; p = tomllib.load(open('pyproject.toml', 'rb'))['project']; \
print('\n'.join(p['dependencies'] + p['optional-dependencies']['dev'] + p['optional-dependencies']['export']))" \
      > requirements.txt && \
    pip install --extra-index-url https://download.pytorch.org/whl/cpu -r requirements.txt
ADD --checksum=sha256:0b5496691403977207f24778695389494a412a76c2cb351e99f024f2a4e3f3ff \
    https://github.com/unitaryai/detoxify/releases/download/v0.4-alpha/multilingual_debiased-0b549669.ckpt \
    /build/checkpoint.ckpt
COPY . .
COPY --from=seed . /seed
RUN python scripts/export_onnx.py --checkpoint /build/checkpoint.ckpt --out /models && \
    rm /models/model.fp32.onnx && \
    python scripts/export_vision.py --out /models --check-images /seed/bursa/images/works/bursa-ulu-camii && \
    rm /models/nsfw.fp32.onnx && \
    python scripts/build_references.py --seed /seed --models /models --out /models/references.npz && \
    python scripts/export_context.py --out /models && \
    rm /models/context.fp32.onnx && \
    python scripts/build_context.py --seed /seed --models /models --out /models/context.npz && \
    MODEL_DIR=/models SEED_DIR=/seed python -m pytest -q

FROM scratch AS models-export
COPY --from=export /models/ /models/

FROM scratch AS models-local
COPY models/model.onnx models/tokenizer.json models/labels.json \
     models/nsfw.onnx models/dinov2.onnx models/vision.json models/references.npz \
     models/context.onnx models/context_tokenizer.json models/context.json models/context.npz /models/

# BuildKit yalnızca seçilen kaynağı derler (local'de export aşaması hiç çalışmaz).
FROM models-${MODEL_SOURCE} AS models

FROM python:${PYTHON_VERSION}-slim AS runtime
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    MODEL_DIR=/app/models \
    ORT_THREADS=1 \
    MAX_CONCURRENCY=2
WORKDIR /app
COPY pyproject.toml ./
RUN --mount=type=cache,id=pip,target=/root/.cache/pip \
    python -c "import tomllib; print('\n'.join(tomllib.load(open('pyproject.toml', 'rb'))['project']['dependencies']))" \
      > requirements.txt && \
    pip install -r requirements.txt && \
    useradd --uid 1654 --no-create-home --shell /usr/sbin/nologin app
COPY app/ app/
COPY --from=models /models/ models/
RUN python -m compileall -q app
EXPOSE 8080
USER 1654
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "1", "--no-access-log"]
