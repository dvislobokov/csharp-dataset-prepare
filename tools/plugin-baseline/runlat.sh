#!/bin/bash
cd /srv/mlbench; M=repo/models
for spec in "cs cs-nn-31m-e2-lr2e3 cs-16384" "cs cs-nn-50m-e3-lr2e3 cs-16384" "go go-nn-31m-e2 go-16384" "go go-nn-50m-e3-lr2e3 go-16384"; do
 set -- $spec
 ./run.sh 0-7 lat $M/$2.cml $M/$3.bpe cases-$1.tsv out/lat-$2.json 8 40 > out/lat-$2.log 2>&1
 taskset -c 0-7 java -Xmx4g -cp "$(cat cp.txt)" io.github.completionml.core.nn.NnBench --file $M/$2.cml --threads 8 --prompt 1500 --gen 20 --runs 20 --reuse 8 > out/nnbench-$2.log 2>&1
done
echo ALLDONE > out/lat.done
