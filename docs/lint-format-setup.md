# 開発環境セットアップ(clone 後 1 回)

依存 CLI ツール(csharpier / husky)を local tool として復元 + pre-commit フックを有効化。
以下 3 コマンドは **リポジトリルートで、上から順に** 実行する(husky は tool restore 後に有効):

```powershell
dotnet tool restore
dotnet husky install
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

- `dotnet tool restore` — csharpier / husky を `.config/dotnet-tools.json` から復元
- `dotnet husky install` — `.husky/pre-commit` を Git hook として有効化
- `git config blame.ignoreRevsFile ...` — 一括整形 commit を `git blame` から除外

commit 時に staged された `*.cs` は自動的に CSharpier で整形される。
CI と `tools/pre-merge-check.ps1` は `dotnet csharpier check` で整形状態を verify する。
`check` が差分を報告したときは `dotnet csharpier format .` で一括再整形すればよい。
アナライザ変更(disable/enable/severity 変更)は `.editorconfig` に**理由コメント必須**で記録すること。
