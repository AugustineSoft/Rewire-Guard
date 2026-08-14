# Exporting AdamCodd/vit-base-nsfw-detector to ONNX

Runs anywhere with Python (Windows, Linux, whatever) -- no Xcode, no Mac needed.
This is the Windows-side replacement for the Core ML conversion script; same model,
different target runtime.

## Recommended: `optimum` (handles graph export + validation for you)

```bash
python3 -m venv venv
venv\Scripts\activate          # or `source venv/bin/activate` on Linux/Mac
pip install optimum[exporters] transformers torch

optimum-cli export onnx ^
  --model AdamCodd/vit-base-nsfw-detector ^
  --task image-classification ^
  onnx_out/
```

This produces `onnx_out/model.onnx`. Copy it into `RewireGuard/Models/model.onnx`.

## Check the label order before trusting the output

Open `onnx_out/config.json` and look at `id2label`. `NsfwClassifier.cs` reads
`appsettings.json`'s `NsfwLabelIndex` to know which output index means "nsfw" --
don't assume it's index 1. Update `appsettings.json` if it's flipped.

## Verify the export actually predicts sensibly

```python
import onnxruntime as ort
from transformers import ViTImageProcessor
from PIL import Image
import numpy as np

processor = ViTImageProcessor.from_pretrained("AdamCodd/vit-base-nsfw-detector")
session = ort.InferenceSession("onnx_out/model.onnx")

img = Image.open("test.jpg").convert("RGB")
inputs = processor(images=img, return_tensors="np")

outputs = session.run(None, {"pixel_values": inputs["pixel_values"]})
logits = outputs[0][0]
probs = np.exp(logits) / np.exp(logits).sum()
print(dict(zip(["label_0", "label_1"], probs)))
```

If this gives nonsense (near-50/50 on an obvious image), the most common cause
is a preprocessing mismatch -- double check `image_mean` / `image_std` in
`preprocessor_config.json` match what `NsfwClassifier.cs` hardcodes (currently
0.5/0.5/0.5 per channel, which is correct for this specific model but won't be
for every ViT checkpoint).

## Fallback: manual torch.onnx.export

If `optimum-cli` gives you trouble, `manual_export.py` in this folder does the
same thing by hand via `torch.onnx.export` with softmax baked in, so the C#
side gets probabilities directly instead of raw logits (you'd then skip the
Softmax step in `NsfwClassifier.cs` if you use this path).
