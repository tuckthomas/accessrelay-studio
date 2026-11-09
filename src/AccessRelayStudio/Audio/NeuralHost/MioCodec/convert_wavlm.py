"""Adapt the official MIT-licensed TorchAudio WavLM Base+ checkpoint for audio.cpp."""

from pathlib import Path
import sys

from safetensors.torch import save_file
import torch


def convert(source: Path, destination: Path) -> None:
    state = torch.load(source, map_location="cpu", weights_only=True)
    tensors = {}

    def copy(destination_name: str, source_name: str) -> None:
        tensors[destination_name] = state[source_name].contiguous()

    conv = "feature_extractor.conv_layers"
    for i in range(7):
        copy(f"{conv}.{i}.0.weight", f"{conv}.{i}.conv.weight")
    for suffix in ("bias", "weight"):
        copy(f"{conv}.0.2.{suffix}", f"{conv}.0.layer_norm.{suffix}")
        copy(f"layer_norm.{suffix}", f"encoder.feature_projection.layer_norm.{suffix}")
        copy(f"post_extract_proj.{suffix}", f"encoder.feature_projection.projection.{suffix}")
        copy(f"encoder.layer_norm.{suffix}", f"encoder.transformer.layer_norm.{suffix}")

    position = "encoder.transformer.pos_conv_embed.conv"
    copy("encoder.pos_conv.0.bias", position + ".bias")
    copy("encoder.pos_conv.0.weight_v", position + ".weight_v")
    copy("encoder.pos_conv.0.weight_g", position + ".weight_g")

    for i in range(12):
        src = f"encoder.transformer.layers.{i}"
        dst = f"encoder.layers.{i}"
        attention = src + ".attention"
        copy(dst + ".self_attn.grep_a", attention + ".gru_rel_pos_const")
        if i == 0:
            copy(dst + ".self_attn.relative_attention_bias.weight", attention + ".rel_attn_embed.weight")

        for suffix in ("bias", "weight"):
            copy(f"{dst}.fc1.{suffix}", f"{src}.feed_forward.intermediate_dense.{suffix}")
            copy(f"{dst}.fc2.{suffix}", f"{src}.feed_forward.output_dense.{suffix}")
            copy(f"{dst}.self_attn_layer_norm.{suffix}", f"{src}.layer_norm.{suffix}")
            copy(f"{dst}.final_layer_norm.{suffix}", f"{src}.final_layer_norm.{suffix}")
            copy(f"{dst}.self_attn.grep_linear.{suffix}", f"{attention}.gru_rel_pos_linear.{suffix}")
            copy(f"{dst}.self_attn.out_proj.{suffix}", f"{attention}.attention.out_proj.{suffix}")
            packed = state[f"{attention}.attention.in_proj_{suffix}"].chunk(3)
            for projection, value in zip(("q", "k", "v"), packed):
                tensors[f"{dst}.self_attn.{projection}_proj.{suffix}"] = value.clone()

    assert len(tensors) == 247
    save_file(tensors, destination)


if __name__ == "__main__":
    convert(Path(sys.argv[1]), Path(sys.argv[2]))
