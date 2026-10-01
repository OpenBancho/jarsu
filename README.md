# jarsu

> **Note**  
> Fork of the original osu! game client customized for compatibility with **[lazer.jar](https://github.com/OpenBancho/lazer.jar)** and the OpenBancho ecosystem.

> [!IMPORTANT]
> **Legal Disclaimer:**  
> Not affiliated with osu! or ppy Pty Ltd.  
> "osu!" is a registered trademark of ppy Pty Ltd.  
> The original codebase is licensed under the MIT License (see [LICENCE](LICENCE)).

---

## Overview

**jarsu** is a desktop-focused fork of the osu! (lazer) client with built-in support for custom backend servers (`lazer.jar` / `bancho.jar`).

### Key Features

- **Custom Server Endpoint Support**: Easily configure and connect to private backend servers via in-game settings.
- **Classic Mod Compatibility**: Seamless interplay with legacy scores and score submissions.
- **Desktop Optimization**: Cleaned and optimized for Windows Desktop environments.

---

## Developing & Building

### Prerequisites

- [.NET 8 / 9+ SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022+, JetBrains Rider, or VS Code with C# Dev Kit.

### Build and Run

Run directly from the command line:

```shell
dotnet run --project osu.Desktop
```

Or open `osu.Desktop.slnf` / `osu.sln` in your IDE and build/run the `osu.Desktop` project.

---

## Licence

The code is licensed under the [MIT licence](LICENCE).  
Copyright (c) 2025 ppy Pty Ltd.
