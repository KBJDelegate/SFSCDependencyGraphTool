"""depgraph: an AI-agent-readable dependency graph for zipped tabular extracts."""

from .infer import infer
from .ingest import ingest
from .model import ColumnStats, Edge, Graph, Node
from .profiles import PROFILES, Profile, get_profile, register
from .render import to_dict, to_dot, to_json, to_mermaid

__version__ = "0.1.0"
__all__ = [
    "ColumnStats", "Edge", "Graph", "Node",
    "PROFILES", "Profile", "get_profile", "register",
    "ingest", "infer",
    "to_dict", "to_json", "to_mermaid", "to_dot",
]
