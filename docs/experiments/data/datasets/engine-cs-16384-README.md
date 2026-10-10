# Engine training shards (cs-16384)

The `corpus` config of this dataset encoded with the plugin engine's tokenizer `cs-16384.bpe`
(https://github.com/dvislobokov/idea-ml-completion, pre-tokenizer go-code-1), in the format of its
`tools/tokenizer/encode_corpus.py`; `tools/nn/train/train.py --data <this folder> --vocab <this folder>/cs-16384.bpe` reads it unchanged.
Folds: `lm` = train split, `validation`, `test` (repository-grouped). BOM not included.

| fold | files | repos | tokens |
|---|---|---|---|
| lm | 4,797,177 | 27,948 | 6,471,602,471 |
| validation | 94,489 | 585 | 117,283,816 |
| test | 97,854 | 584 | 129,596,677 |
