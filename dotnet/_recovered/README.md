# `_recovered/` — uncompiled reference from git `7b16806`

These files are the original .NET scaffold recovered from commit `7b16806`. They were moved out of
the live projects during **B0** because they reference a Data layer (`QaAutomation.Infrastructure.Data.AppDbContext`)
and models that **were never in that commit** — the scaffold is ~40% complete and does **not compile**.

**They are deliberately outside every `.csproj` and are not part of the build.**

## How to use them
As each migration phase lands (spec §6), copy the relevant file back into its project and rewrite it
to match the **live Python backend** (`../../backend/`), which is the authoritative source of truth.
Treat this scaffold as a *shape hint only* — per the spec's Ground Rule #1, never trust it over Python;
copy frozen literals (prompts, ADO bodies, Steps XML, activity strings) verbatim from Python and
golden-diff them.

```
Infrastructure/   Agents/, AzureDevOps/, Knowledgebase/, Llm/, Reports/, DependencyInjection.cs
Api/              Controllers/   (AuthorTests, Diagnostics, Knowledgebase, Settings)
```

You can also re-extract any original file directly: `git show 7b16806:src/<path>`.
