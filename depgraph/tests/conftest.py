from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent))
from make_fixture import build, build_split  # noqa: E402


@pytest.fixture(scope="session")
def extract(tmp_path_factory) -> Path:
    """One zip, one xlsx per object."""
    return build(tmp_path_factory.mktemp("fx") / "extract.zip")


@pytest.fixture(scope="session")
def split_extract(tmp_path_factory) -> Path:
    """The same org as a zip of two zips, with Contact and Task split between them."""
    return build_split(tmp_path_factory.mktemp("split") / "export.zip")
