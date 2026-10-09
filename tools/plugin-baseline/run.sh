#!/bin/bash
# usage: run.sh <cpuset> <args...>
set=$1; shift
exec taskset -c $set java -Xmx6g -cp "$(cat /srv/mlbench/cp.txt)" io.github.completionml.core.nn.PluginBaseline "$@"
