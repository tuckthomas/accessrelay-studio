# Marketplace

The separate [accessrelay-marketplace repository](https://github.com/tuckthomas/accessrelay-marketplace)
owns listings, versioned implementation packages, game source, capture models,
training workflows and licenses. Studio owns browsing, type/game filters, download
integrity checks, installation and the generic plugin host.

Controller scripts and profiles use schema/API 1. Actual .NET/UI/training extensions
use [schema/API 2](installed-game-plugins.md), with checked implementation resources
and trusted-code metadata. A training plugin becomes selectable in Model Training
after its implementation installs and loads. Gameplay window and training folder
choices remain host settings.

NBA 2K27 Model Training 1.1.0 contains its C# runtime, UI, training tools and licensed
pose/OCR capture models. It contains no trained shot-release model. The separate
NBA controller script does not itself enable training. No game implementation or
downloadable sample package is bundled with Studio.
