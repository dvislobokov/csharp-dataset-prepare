# Engine training shards (go-16384)

The `corpus` config of this dataset encoded with the plugin engine's tokenizer `go-16384.bpe`
(https://github.com/dvislobokov/idea-ml-completion, pre-tokenizer go-code-1), in the format of its
`tools/tokenizer/encode_corpus.py`; `tools/nn/train/train.py --data <this folder> --vocab <this folder>/go-16384.bpe` reads it unchanged.
Folds: `lm` = train split, `validation`, `test` (repository-grouped). BOM not included.

| fold | files | repos | tokens |
|---|---|---|---|
| lm | 4,728,231 | 41,688 | 10,420,869,308 |
| validation | 132,019 | 825 | 264,356,282 |
| test | 89,513 | 843 | 216,947,922 |
