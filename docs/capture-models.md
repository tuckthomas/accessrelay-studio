# Automatic capture model assets

The NBA adapter uses these generic pretrained models for acquisition, not shot
timing. They have not been trained or certified on NBA 2K27 gameplay.

- RTMO-t (body7, 416x416), OpenMMLab/MMPose:
  https://github.com/open-mmlab/mmpose/tree/main/projects/rtmo
  Model SHA-256: `20aad6e2e42359cac1c5b4a0b2da00e29bfe91a72a782fdcf287d273a04c1b24`.
- English PP-OCRv3 mobile text detection and PP-OCRv4 mobile recognition,
  PaddleOCR exports distributed by RapidAI/RapidOCR:
  https://github.com/RapidAI/RapidOCR and https://github.com/PaddlePaddle/PaddleOCR.
  Their pinned upstream hashes and URLs are in `Install-NbaCaptureModels.ps1`.

MMPose, RTMLib, RapidOCR and PaddleOCR are Apache-2.0 projects. Their applicable
copyright/license terms remain with the upstream code, models and training data.
The C# preprocessing/decoding implements their documented BGR letterbox, DB text
probability map and CTC conventions; no Python worker is used for live capture.
See https://github.com/open-mmlab/mmpose/blob/main/LICENSE,
https://github.com/Tau-J/rtmlib/blob/main/LICENSE,
https://github.com/RapidAI/RapidOCR/blob/main/LICENSE and
https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE.

`dotnet publish` bundles all three hash-verified ONNX files with the application
through `scripts/Install-NbaCaptureModels.ps1`. Published installations include
the models, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and a source/hash `models.json`.
The user does not need to download models or run a separate installer for capture.
The publishing machine keeps downloads under project-local artifacts; after the
first download, a verified cache supports publishing without a network connection.
Model weights remain ignored by the application Git repository and ship inside
the published application distribution. This adds approximately 35.7 MiB.
The native loader verifies each file again before inference. The installed training
adapter owns identity association, meter proposals, floor estimation and HUD rules.

CPU and CUDA acquisition are explicit choices inherited from the analysis settings.
CUDA acquisition allows ORT's CPU shape/NMS operators and records this mixed policy
as `CUDAExecutionProvider+CPU-operators`. It does not authorize release-model arming
or certify latency. Release-model acceptance remains separate and observation-only
for the automatic region policy.
