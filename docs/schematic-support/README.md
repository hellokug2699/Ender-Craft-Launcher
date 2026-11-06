# Schematic Support Rules

- Status: Active baseline for nightly
- Last reviewed: 2026-11-06
- Machine-readable rules: [rules.json](rules.json)
- Scope: Client-side Java Edition projection/schematic capability detection

## 1. Purpose

This directory defines how ECL decides whether a Minecraft instance has a known schematic/projection
provider. It is a compatibility knowledge base, not a guarantee that every schematic file will load or
render correctly.

The rules are intentionally loader- and Minecraft-version-aware. Do not infer support from a file
extension, a folder name, or a similarly named JAR. Format recognition and in-game support are two
different questions (`hints/ui/pages/schematics.md` §3–§4).

## 2. Recognized file formats

| Format | Extensions | Typical folders |
|---|---|---|
| Litematica | `.litematic` | `schematics/` |
| Sponge schematic | `.schem` | `config/worldedit/schematics/` |
| MCEdit schematic | `.schematic` | `config/worldedit/schematics/` |
| NBT structure | `.nbt` | `schematics/` (Create) |
| Axiom blueprint | `.bp` | `config/axiom/blueprints/` |

An extension only identifies a candidate. Litematica/Sponge/MCEdit/Create files must additionally match
a gzip or uncompressed NBT container signature. Axiom blueprints have no publicly documented stable
signature, so ECL reports them as “unverified” instead of claiming the format is confirmed.

## 3. Supported provider families

### Litematica

- Client-side schematic mod for Fabric on Minecraft 1.14+; there is a Forge build only for 1.12.2.
- Requires MaLiLib for the builds covered by this rule. Check the exact installed Litematica artifact’s
  mandatory dependencies; do not infer the dependency from the project name alone.
- Third-party ports (for example Forgematica) have separate mod ids and must be listed separately.

### Forgematica

- Unofficial (Neo)Forge port of Litematica.
- Requires MaFgLib for the versions covered by this rule.
- Only accept it when the exact installed artifact declares the instance’s loader and Minecraft version.

### WorldEdit

- Saves Sponge schematics (`.schem`) and legacy MCEdit schematics (`.schematic`).
- Mod builds exist for Fabric, Forge and NeoForge. Validate the exact installed artifact’s loader and
  Minecraft metadata; a project-level loader list is not proof for every release.
- The schematic directory is `config/worldedit/schematics/` for modded clients.

### Create

- Loads `.nbt` structures from the instance’s `schematics/` folder through its Schematic Table.
- The CurseForge/Modrinth “Create” project targets Forge/NeoForge; a separately published Fabric port
  exists. Validate the exact artifact instead of assuming one loader family.
- Do not require Flywheel separately when the installed Create artifact bundles it; follow the exact
  artifact’s mandatory dependencies.

### Axiom

- Saves `.bp` blueprints under `config/axiom/blueprints/`.
- The published Axiom project targets Fabric and declares Fabric API as a requirement.
- Blueprint structure is not publicly specified in a stable, verifiable form; ECL must not parse or
  preview it as NBT.

## 4. Decision model

For each recognized format the detector returns one of:

- `supported`: at least one recognized provider is installed, its exact artifact metadata matches the
  instance’s Minecraft version and loader, and its mandatory dependencies are satisfied.
- `missing`: no recognized provider for that format is installed.
- `incomplete`: a provider is installed and compatible, but a mandatory dependency is missing or its
  version range is not satisfied.
- `incompatible`: a provider is installed but its declared Minecraft version or loader does not match.
- `conflict`: two or more compatible providers claim the same format; ECL cannot prove which one will
  actually handle the file.
- `unknown`: metadata is insufficient (for example no Minecraft dependency declared, or an unrecognized
  loader/version combination).

The overall result is `supported` when at least one format is supported. The page must still show the
per-format states, because “Litematica works” does not imply “WorldEdit schematics work”.

Support detection never blocks listing, deleting, exporting or importing schematic files. It is not a
3D rendering capability: the in-app 3D viewer requires a compatible rendering Addition, and the NBT
Explorer is a separate official Addition. Neither is implied by a supported provider.

## 5. Detection requirements

1. Read the instance’s actual Minecraft version, loader identity and loader version.
2. Inspect enabled mod metadata and version constraints, not filenames.
3. Resolve mandatory dependencies from the exact installed artifact metadata.
4. Match provider rules by mod id and format; validate loader/Minecraft compatibility from metadata.
5. Return `unknown` whenever metadata cannot prove support.
6. Refresh after mod install/removal or instance changes.
7. Never present a supported format as a guarantee that an individual file is valid.

## 6. Rule maintenance

- Keep this document, `rules.json` and `SchematicSupportDetector` in sync.
- Prefer authoritative project pages and per-file metadata.
- Record source URLs and review dates for every rule change.
- Re-review when a provider publishes support for a new Minecraft version or loader.
- Do not turn an unverified community port into a supported rule without checking its exact artifacts,
  dependencies and loader declaration.

## 7. Sources reviewed

- Litematica project (formats, loader history, MaLiLib): https://modrinth.com/mod/litematica ,
  https://github.com/maruohon/litematica/wiki
- MaLiLib project: https://modrinth.com/mod/malilib
- Forgematica project (Neo/Forge port, MaFgLib): https://modrinth.com/mod/forgematica
- WorldEdit schematic folder and formats: https://www.world-edit.com/schematics-folder/
- WorldEdit project loaders: https://modrinth.com/mod/worldedit
- Create schematic folder and `.nbt` format:
  https://wiki.createmod.net/users/schematics/loading-and-positioning-schematics
- Axiom blueprint browser and folder: https://axiomdocs.moulberry.com/editor/windows/blueprints.html
- Axiom project (Fabric, Fabric API): https://modrinth.com/mod/axiom

Upstream metadata changes over time; re-check these sources when updating the rules.
