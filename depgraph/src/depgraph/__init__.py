"""depgraph: an AI-agent-readable dependency graph for zipped tabular extracts."""

# Defined before the imports below, which read it.
__version__ = "0.2.0"

from .docs import write_docs  # noqa: E402
from .infer import infer  # noqa: E402
from .ingest import (  # noqa: E402
    filter_members, ingest, list_sources, plan_parts, read_name_filter,
)
from .model import ColumnStats, Edge, Graph, Member, Node  # noqa: E402
from .profiles import PROFILES, Profile, get_profile, register  # noqa: E402
from .render import to_dict, to_dot, to_json, to_mermaid  # noqa: E402

__all__ = [
    "ColumnStats", "Edge", "Graph", "Member", "Node",
    "PROFILES", "Profile", "get_profile", "register",
    "ingest", "infer", "list_sources", "plan_parts", "read_name_filter",
    "filter_members", "to_dict", "to_json", "to_mermaid", "to_dot", "write_docs",
]
