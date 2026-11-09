# Dialogue separation

The Remove background music action keeps the dialogue stem estimated by MRX. The model separates dialogue, music, and effects. It runs on the CPU through ONNX Runtime and uses only the chosen clip range.

The optional `mrx.int8.onnx` is the [audiojs ONNX export](https://huggingface.co/audiojs/mrx/tree/dedc823a0bea2ed085d19e8b7f32589d35169ee8) of [MERL’s pretrained MRX checkpoint](https://github.com/merlresearch/cocktail-fork-separation/tree/19b3de827ebc4bfb014570cf92dd32b4ee3b6921). [MERL lists the checkpoint under the MIT license.](https://github.com/merlresearch/cocktail-fork-separation/blob/19b3de827ebc4bfb014570cf92dd32b4ee3b6921/.reuse/dep5) See `MRX.LICENSE.txt` for the license text.

The model file has SHA-256 `d876c92d224f5d8cd2207d528b23369da895a6de71fb24132416fcb16f9f8424`.

MRX comes from [The Cocktail Fork Problem: Three-Stem Audio Separation for Real-World Soundtracks](https://arxiv.org/abs/2110.09958) by Darius Petermann, Gordon Wichern, Zhong-Qiu Wang, and Jonathan Le Roux.
