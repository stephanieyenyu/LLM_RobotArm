# Part A: YOLO and QRCode Detection

This folder contains the C# implementation for Part A of the LLM robot arm framework.

The purpose of Part A is to detect objects and QRCode positioning markers from an input image, then export the result as a JSON file for the coordinate mapping stage.

## Current Scope

The current version supports:

- Reading `images/test_scene.jpg`
- Detecting QRCode markers named `QR1`, `QR2`, and `QR3`
- Detecting common objects using a YOLO ONNX model
- Exporting detection results to `outputs/detection_result.json`
- Exporting a visual debug image to `outputs/visual_result.jpg`

## Input Image

The program reads this file:

```text
csharp_server/images/test_scene.jpg
