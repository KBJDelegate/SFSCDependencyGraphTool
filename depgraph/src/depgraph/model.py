"""Data model for the dependency graph.

Everything here is plain dataclasses so it serialises to JSON without a schema
library, and so worker processes can ship results back cheaply.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import PurePosixPath
from typing import Any


@dataclass(frozen=True)
class Member:
    """One tabular file somewhere in the extract, possibly inside nested zips."""

    #: Local file to read: the zip that holds the member, or the file itself.
    path: str
    #: Name inside the zip at ``path``; None when ``path`` is a plain file.
    name: str | None
    #: Where it came from, for humans: "outer.zip!WE_2.zip!Account.csv".
    label: str
    size: int = 0
    #: CRC-32 from the zip directory, so identical copies are spotted unread.
    crc: int | None = None

    @property
    def basename(self) -> str:
        return PurePosixPath((self.name or self.path).replace("\\", "/")).name


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
    #: Smallest and largest value, for numbers and dates.
    min: Any = None
    max: Any = None
    #: Longest value in characters, for text.
    max_length: int | None = None
    #: Every value with its row count, kept only while the column has few
    #: distinct values (picklists, flags). None means "too many to list".
    top_values: dict[str, int] | None = None

    @property
    def non_null(self) -> int:
        return self.rows - self.nulls

    @property
    def unique(self) -> bool:
        return self.non_null > 0 and self.distinct == self.non_null


@dataclass
class Node:
    """One object in the extract: a table, possibly read from several files."""

    id: str
    source: str
    sheet: str
    rows: int
    columns: list[ColumnStats] = field(default_factory=list)
    key: str | None = None
    key_token: str | None = None
    #: column name -> parquet path holding (value, row_count) for id-like columns
    staged: dict[str, str] = field(default_factory=dict)
    #: column name -> parquet of distinct value hashes; only for split objects,
    #: where it lets distinct counts be combined exactly across the parts.
    hashed: dict[str, str] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)
    #: Labels of every file this object was read from; more than one when an
    #: export split it across several zips.
    parts: list[str] = field(default_factory=list)
    size_bytes: int = 0

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

    def node(self, node_id: str) -> Node | None:
        return next((n for n in self.nodes if n.id == node_id), None)
