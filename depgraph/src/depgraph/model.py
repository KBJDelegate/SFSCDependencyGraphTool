"""Data model for the dependency graph.

Everything here is plain dataclasses so it serialises to JSON without a schema
library, and so worker processes can ship results back cheaply.
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class ColumnStats:
    name: str
    dtype: str
    rows: int
    nulls: int
    distinct: int
    #: For profiles that encode the target type inside the value (Salesforce key
    #: prefixes), the observed tokens mapped to how many rows carried them.
    id_tokens: dict[str, int] = field(default_factory=dict)
    samples: list[str] = field(default_factory=list)
    is_id_like: bool = False

    @property
    def non_null(self) -> int:
        return self.rows - self.nulls

    @property
    def unique(self) -> bool:
        return self.non_null > 0 and self.distinct == self.non_null


@dataclass
class Node:
    """One sheet in the extract: a table."""

    id: str
    source: str
    sheet: str
    rows: int
    columns: list[ColumnStats] = field(default_factory=list)
    key: str | None = None
    key_token: str | None = None
    #: column name -> parquet path holding (value, row_count) for id-like columns
    staged: dict[str, str] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)

    def column(self, name: str) -> ColumnStats | None:
        return next((c for c in self.columns if c.name == name), None)


@dataclass
class Edge:
    from_node: str
    from_column: str
    to_node: str
    to_column: str
    kind: str  # "lookup" | "self" | "name-only"
    cardinality: str  # "N:1" | "1:1"
    confidence: float
    resolve_rate: float
    null_pct: float
    #: True when the references were actually found in the target key, i.e. the
    #: relationship is proven by data rather than inferred from naming.
    verified: bool = False
    evidence: list[str] = field(default_factory=list)
    polymorphic_group: str | None = None


@dataclass
class Unresolved:
    node: str
    column: str
    token: str | None
    rows: int
    reason: str


@dataclass
class Graph:
    nodes: list[Node] = field(default_factory=list)
    edges: list[Edge] = field(default_factory=list)
    unresolved: list[Unresolved] = field(default_factory=list)
    profile: str = "generic"
    source: str = ""
    stats: dict = field(default_factory=dict)
    load_order: list[list[str]] = field(default_factory=list)
    cycles: list[list[str]] = field(default_factory=list)
    #: Sheets the export shipped with no rows. Kept as bare names: they have no
    #: columns, key or references, so as nodes they were pure noise.
    empty_sheets: list[str] = field(default_factory=list)

    def node(self, node_id: str) -> Node | None:
        return next((n for n in self.nodes if n.id == node_id), None)
