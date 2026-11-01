import numpy as np

from app.context import ContextIndex


def _unit(*rows: list[float]) -> np.ndarray:
    vectors = np.asarray(rows, dtype=np.float32)
    return vectors / np.linalg.norm(vectors, axis=1, keepdims=True)


def _index() -> ContextIndex:
    # "a" eseri: iki parça (merkez x-y ortası); prototipler z ekseni çevresinde (+ bir uzak prototip).
    chunks = _unit([1, 0, 0], [0, 1, 0], [0, 0, 1])
    prototypes = _unit([0, 0, 1], [0, 0.2, 1], [0.2, 0, 1], [0, 0.4, 1], [0.4, 0, 1], [1, 1, -1])
    return ContextIndex(np.asarray(["a", "a", "b"]), chunks, prototypes, "v1")


def test_info_counts_works_and_prototypes() -> None:
    info = _index().info
    assert (info.version, info.works, info.prototypes) == ("v1", 2, 6)


def test_monument_relevance_is_cosine_to_chunk_centroid() -> None:
    (result,) = _index().relevance(_unit([1, 1, 0]), "a")
    assert result.monument == 1.0
    assert result.score == 1.0


def test_genre_relevance_is_mean_of_top_five_prototypes() -> None:
    query = _unit([0, 0, 1])
    (result,) = _index().relevance(query, "a")
    expected = (_unit([0, 0, 1], [0, 0.2, 1], [0.2, 0, 1], [0, 0.4, 1], [0.4, 0, 1]) @ query[0]).mean()
    assert result.genre == round(float(expected), 4)
    assert result.monument == 0.0
    assert result.score == result.genre


def test_unknown_monument_uses_genre_only() -> None:
    (result,) = _index().relevance(_unit([1, 1, 0]), "bilinmeyen")
    assert result.monument is None
    assert result.score == result.genre


def test_batch_keeps_order() -> None:
    results = _index().relevance(_unit([1, 1, 0], [0, 0, 1]), "a")
    assert [r.monument for r in results] == [1.0, 0.0]


def test_damage_signal_scores_relevance_and_categories() -> None:
    damage = _unit([1, 0, 0], [1, 0.1, 0], [0, 0, 1], [0, 0.1, 1])
    labels = np.asarray(["theft", "theft", "vandalism", "vandalism"])
    index = ContextIndex(np.asarray(["a"]), _unit([1, 0, 0]), _unit([0, 1, 0]), "v1", damage, labels)
    (signal,) = index.damage(_unit([1, 0, 0]))
    assert index.info.damage_prototypes == 4
    assert signal.categories["theft"] > 0.99
    assert signal.categories["vandalism"] < 0.01
    assert signal.relevance == round(float((damage @ _unit([1, 0, 0])[0]).mean()), 4)


def test_index_without_damage_prototypes_returns_none() -> None:
    assert _index().damage(_unit([1, 0, 0])) is None
    assert _index().info.damage_prototypes == 0
