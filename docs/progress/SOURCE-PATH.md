# Arena source path (lead note, 2026-09-13 22:50)

The EFT Arena client now lives locally at `D:/Battlestate Games/Escape from Tarkov Arena`
(0.5.4.0-46834, verified identical to the former `X:` share: same ConsistencyInfo SHA256 and
file sizes). The `X:` network share is being disconnected.

- Use `--source "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"`.
- `tools/extract/extract_equator.py` DEFAULT_SOURCE and both package manifests
  (`work/extract/equator*/manifest.json` -> `source.arena_data_root`) were switched to this path.
- The local install is READ-ONLY for this project, exactly like `X:` was.
