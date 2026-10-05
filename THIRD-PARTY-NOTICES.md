# Third-party components

WinBootVideo embeds the unmodified official `HackBGRT-2.6.0.zip` from [Metabolix/HackBGRT](https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0), SHA-256 `6204911d777ac03e514b90126568ef6faa1d477779b31c536b142dba92f4a2c0`. Extraction retains `LICENSE`, `README.efilib` and `shim-signed/COPYRIGHT`. Custom image and configuration are generated separately; official setup/EFI/shim binaries remain unmodified.

HackBGRT copyright 2016 Lauri Kenttä, MIT license. Boot entry cloning follows [EfiBootEntries.cs](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/src/EfiBootEntries.cs); bounds checks, journal ownership, registration order and rollback are WinBootVideo additions. Official GNU-EFI/Intel and shim notices are reproduced in the distributed `licenses/` folder. Sources and library references remain available in the [pinned repository](https://github.com/Metabolix/HackBGRT/tree/62cc4a8cb7d2c7b7e2797f4c7480f00d0774e768).

Licenses do not establish firmware compatibility or eliminate boot risks. First-boot trust confirmation remains a user action under the upstream shim workflow.
