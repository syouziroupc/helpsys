# HelpSys

HelpSys is a Windows desktop assistance system that guides a person through computer tasks without operating the computer on their behalf.

## Core policy

- **Human-in-the-loop:** HelpSys points, explains, and verifies. The user performs every click and key press.
- **Precision first:** Prefer Windows UI Automation metadata and coordinates. Use vision/OCR only when structured UI data is insufficient.
- **Silent by default:** While idle, HelpSys does not capture the screen and does not call an AI service.
- **Explicit activation only:** Assistance starts from the HelpSys button, the global hotkey (`Ctrl+Alt+H`), or a future on-device wake word.
- **One action at a time:** Guidance advances only after the user performs the current step and HelpSys observes the resulting UI state.

## Initial Windows prototype

The first implementation is a .NET 10 WPF desktop application. It provides:

- a small always-on-top HelpSys launcher;
- a global activation hotkey;
- Windows UI Automation scanning;
- target selection from accessible UI metadata;
- a click-through overlay that highlights the target and shows a short instruction;
- no mouse or keyboard injection.

The first prototype intentionally keeps voice wake-word detection and remote AI planning behind interfaces. Those features should be added only after the local observation/overlay loop is stable.

## Architecture

```text
Explicit activation
      |
      v
Command input
      |
      v
Guide planner
      |
      v
UI Automation snapshot ----> Vision/OCR fallback (later)
      |
      v
Target + bounding rectangle
      |
      v
Click-through overlay
      |
      v
Human performs action
      |
      v
Observe changed UI -> next single step
```

## Build

Requirements:

- Windows 10/11
- .NET 10 SDK

```powershell
dotnet build HelpSys.sln
dotnet run --project src/HelpSys.Desktop/HelpSys.Desktop.csproj
```

## Current status

This repository contains the initial Windows desktop skeleton. The next milestone is to add a structured planner endpoint and a local wake-word engine while preserving the rule that no screenshot or AI request occurs before activation.
