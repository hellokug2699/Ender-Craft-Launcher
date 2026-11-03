# Shader Support Rules

- Status: Active baseline for nightly
- Last reviewed: 2026-11-03
- Machine-readable rules: [rules.json](rules.json)
- Scope: Client-side Java Edition shader-pack capability detection

## 1. Purpose

This directory defines how ECL decides whether a Minecraft instance has a known shader-pack loader. It is a compatibility knowledge base, not a guarantee that every shader pack will render correctly.

The rules are intentionally loader- and Minecraft-version-aware. Do not infer support merely from the presence of a performance mod, a shader-pack file, or a similarly named JAR.

## 2. Supported provider families

### OptiFine

- OptiFine includes its own shader-pack support.
- For Forge instances, count OptiFine only when the installed OptiFine build is for the exact Minecraft version and its published Forge compatibility metadata matches the installed Forge build.
- Do not infer Forge compatibility from the existence of an OptiFine download alone. The official downloads page may list a build while showing Forge as N/A.
- Do not count OptiFine as a Fabric or NeoForge shader provider by default. Do not infer compatibility through OptiFabric or unrelated compatibility bridges unless a future rule explicitly validates that combination.

### Oculus

- Oculus is an unofficial Iris fork for FML-based environments.
- Its currently declared Modrinth game-version set is discrete: 1.16.5, 1.18.2, 1.19, 1.19.2, 1.19.4, 1.20 and 1.20.1. The exact artifact must also declare the current loader (Forge or NeoForge); never assume every listed version is available for both loaders.
- For Forge, Oculus is the principal non-OptiFine shader-provider path in the listed legacy versions.
- For NeoForge, only accept Oculus when the exact file metadata declares NeoForge for that Minecraft version.
- Check the exact Oculus file's required dependencies. Embeddium/Rubidium are common renderer companions, but do not hard-code one dependency for every Oculus release unless that file's metadata declares it required.
- Oculus alone being present is not enough when a required dependency is missing or incompatible.

### Iris

- Iris officially supports Fabric on Minecraft 1.16.5 and higher.
- Iris officially supports NeoForge on Minecraft 1.21.1 and higher.
- Iris does not officially support Forge.
- Iris ships with Sodium; do not require a separately installed Sodium JAR when the installed Iris release bundles it.
- Validate the exact Iris file against the Minecraft version and loader. Do not treat a project-level broad version claim as proof that every release supports every loader/version combination.

## 3. Decision model

The detector should return one of:

- `supported`: at least one recognized provider is installed and its exact Minecraft/loader compatibility and required dependencies are satisfied.
- `missing`: no recognized provider is installed for this loader/version.
- `incomplete`: a provider is detected, but a required dependency is missing or incompatible.
- `incompatible`: a recognized provider is installed but its declared Minecraft version or loader does not match the instance.
- `conflict`: mutually incompatible provider families are detected together (for example, OptiFine alongside Iris).
- `unknown`: metadata is insufficient, the loader/version is outside the maintained rule set, or the installed component cannot be confidently identified.

Only `supported` suppresses the missing-support warning. `incomplete`, `incompatible`, `conflict`, and `unknown` must not be reported as supported. Warning copy should explain the actual detected condition rather than always saying “OptiFine is missing”.

This is a capability check, not a shader-pack compatibility test. Even with a supported provider, individual shader packs may be unsupported or fail due to GPU, driver, other rendering mods, or pack-specific requirements.

## 4. Detection requirements

1. Read the instance's actual Minecraft version and loader identity/version.
2. Inspect installed client-side mod metadata and version constraints, not filenames alone.
3. Resolve dependency requirements from the exact installed artifact metadata where available.
4. Match rules by exact Minecraft version, loader family, and provider artifact compatibility.
5. Detect known conflicting provider combinations.
6. If metadata cannot prove support, return `unknown` rather than guessing.
7. Refresh the result after a mod scan, install, removal, or instance change.
8. Do not block importing, downloading, ordering, enabling, or deleting shader packs solely because shader support is missing.

## 5. Rule maintenance

- Keep this document and `rules.json` in sync.
- Prefer authoritative project pages and per-file metadata from the mod author/platform.
- Record source URLs and review dates for every rule change.
- Treat project-level compatibility as discovery information; exact release metadata is the final authority.
- Re-review when a provider publishes support for a new Minecraft version or loader.
- Never turn an unverified community port into a supported rule without checking its exact artifacts, dependencies, and loader declaration.

## 6. Sources reviewed

- Iris official FAQ and support matrix: https://irisshaders.dev/
- Iris Modrinth project: https://modrinth.com/mod/iris
- Oculus Modrinth project and compatibility metadata: https://modrinth.com/mod/oculus
- OptiFine official downloads and Forge compatibility column: https://optifine.net/downloads
- Oculus CurseForge files: https://www.curseforge.com/minecraft/mc-mods/oculus/files/all

The upstream metadata changes over time. These sources should be checked again when updating the rules.
