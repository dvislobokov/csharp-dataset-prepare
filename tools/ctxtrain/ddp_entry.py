"""torchrun entry for the engine's train.py: its arguments come from $TRAIN_ARGS, because torch.distributed.run's argparse
(abbreviations on) rejects train.py options such as --run as ambiguous with its own --run-path."""
import os
import runpy
import shlex
import sys

script = os.environ.get("ENTRY_SCRIPT", "train.py")      # train.py (cwd = engine tools/nn/train) or finetune.py
sys.argv = [script] + shlex.split(os.environ["TRAIN_ARGS"])
sys.path.insert(0, os.path.dirname(os.path.abspath(script)))
runpy.run_path(script, run_name="__main__")
