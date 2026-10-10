"""torchrun entry for the engine's train.py: its arguments come from $TRAIN_ARGS, because torch.distributed.run's argparse
(abbreviations on) rejects train.py options such as --run as ambiguous with its own --run-path."""
import os
import runpy
import shlex
import sys

sys.argv = ["train.py"] + shlex.split(os.environ["TRAIN_ARGS"])
sys.path.insert(0, os.getcwd())
runpy.run_path("train.py", run_name="__main__")
