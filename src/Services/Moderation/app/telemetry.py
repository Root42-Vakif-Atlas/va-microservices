"""OpenTelemetry: .NET servisleriyle aynı kaynak öznitelikleri; yalnızca OTEL_EXPORTER_OTLP_ENDPOINT
tanımlıysa dışa aktarılır (yerel geliştirme ve testte kapalı)."""

from __future__ import annotations

import logging
import os

from fastapi import FastAPI

_histogram = None


def instrument(app: FastAPI) -> None:
    global _histogram
    logging.basicConfig(
        level=logging.INFO, format='{"level":"%(levelname)s","logger":"%(name)s","message":"%(message)s"}'
    )
    endpoint = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT")
    if not endpoint:
        return

    from opentelemetry import metrics, trace
    from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
    from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
    from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
    from opentelemetry.sdk.metrics import MeterProvider
    from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
    from opentelemetry.sdk.resources import Resource
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import BatchSpanProcessor

    resource = Resource.create(
        {
            "service.name": os.environ.get("OTEL_SERVICE_NAME", "moderation"),
            "service.namespace": "vakif-atlas",
        }
    )
    tracer = TracerProvider(resource=resource)
    tracer.add_span_processor(BatchSpanProcessor(OTLPSpanExporter(endpoint=endpoint, insecure=True)))
    trace.set_tracer_provider(tracer)
    interval = int(os.environ.get("OTEL_METRIC_EXPORT_INTERVAL", "15000"))
    reader = PeriodicExportingMetricReader(OTLPMetricExporter(endpoint=endpoint, insecure=True), interval)
    metrics.set_meter_provider(MeterProvider(resource=resource, metric_readers=[reader]))
    _histogram = metrics.get_meter("VakifAtlas.Moderation").create_histogram(
        "vakifatlas.moderation.inference.duration", unit="s", description="Skorlama süresi (istek başına)"
    )
    # Sağlık yoklamaları iz üretmez.
    FastAPIInstrumentor.instrument_app(app, excluded_urls="health/.*")


def record_inference(seconds: float, items: int, images: int = 0) -> None:
    if _histogram is not None:
        _histogram.record(seconds, {"items": items, "images": images})
