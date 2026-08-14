"""
Fallback manual ONNX export for AdamCodd/vit-base-nsfw-detector, in case
`optimum-cli export onnx` isn't available or gives you trouble.

Bakes softmax into the graph so NsfwClassifier.cs could skip its own softmax
step if you use this output instead of the optimum-cli one (they're not both
needed -- pick one path).

Setup:
    python3 -m venv venv
    venv\\Scripts\\activate
    pip install torch transformers onnx

Then:
    python3 manual_export.py
"""

import torch
from transformers import ViTImageProcessor, AutoModelForImageClassification

MODEL_ID = "AdamCodd/vit-base-nsfw-detector"
OUTPUT_PATH = "model.onnx"

print(f"Downloading {MODEL_ID}...")
processor = ViTImageProcessor.from_pretrained(MODEL_ID)
model = AutoModelForImageClassification.from_pretrained(MODEL_ID)
model.eval()

image_size = model.config.image_size
if isinstance(image_size, (list, tuple)):
    image_size = image_size[0]
print(f"Input size: {image_size}x{image_size}")
print(f"Labels: {model.config.id2label}")
print(f"Preprocessor mean/std: {processor.image_mean} / {processor.image_std}")


class WrappedModel(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, pixel_values):
        logits = self.model(pixel_values=pixel_values).logits
        return torch.nn.functional.softmax(logits, dim=-1)


wrapped = WrappedModel(model)
wrapped.eval()

dummy_input = torch.rand(1, 3, image_size, image_size)

torch.onnx.export(
    wrapped,
    dummy_input,
    OUTPUT_PATH,
    input_names=["pixel_values"],
    output_names=["probabilities"],
    dynamic_axes=None,  # fixed batch size of 1; simplest for a desktop app
    opset_version=17,
)

print(f"Saved {OUTPUT_PATH}")
print("Copy this into RewireGuard/Models/model.onnx")
