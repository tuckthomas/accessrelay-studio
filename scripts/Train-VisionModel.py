"""Compatibility entry point; NBA-specific training lives in games/nba2k27/training."""
from pathlib import Path
import runpy
import sys

_pack_training = Path(__file__).resolve().parents[1] / "games" / "nba2k27" / "training"
sys.path.insert(0, str(_pack_training))
globals().update(runpy.run_path(str(_pack_training / "Train-VisionModel.py"), run_name=__name__))
