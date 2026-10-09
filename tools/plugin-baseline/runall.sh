#!/bin/bash
cd /srv/mlbench; M=repo/models; mkdir -p out
# 4 models sequentially, 8 workers x 1 thread each on cores 0-7 (accuracy does not depend on cores)
for spec in "cs cs-nn-31m-e2-lr2e3 cs-16384" "cs cs-nn-50m-e3-lr2e3 cs-16384" "go go-nn-31m-e2 go-16384" "go go-nn-50m-e3-lr2e3 go-16384"; do
 set -- $spec
 ./run.sh 0-15 acc $M/$2.cml $M/$3.bpe cases-$1.tsv out/acc-$2-test.tsv 16 > out/acc-$2-test.log 2>&1
done
echo ALLDONE > out/acc-test.done
