"""Optional PyTorch experiments. Imports stay lazy for stdlib provenance tools."""
HEADS = ("global-pool", "ordered-head")
UNCERTAINTIES = ("residual", "gaussian-nll", "staged-residual")


def uncertainty_semantics(mode):
    if mode not in UNCERTAINTIES: raise ValueError("Unsupported uncertainty experiment")
    return "gaussian-95-half-width-v1" if mode == "gaussian-nll" else "absolute-error-estimate-v1"


def build_model(history, head="global-pool", uncertainty="residual"):
    if not 2 <= history <= 16 or head not in HEADS or uncertainty not in UNCERTAINTIES:
        raise ValueError("Unsupported temporal experiment configuration")
    import torch
    from torch import nn

    class TemporalReleaseModel(nn.Module):
        def __init__(self):
            super().__init__()
            self.features = nn.Sequential(
                nn.Conv3d(3, 8, 3, stride=(1, 2, 2), padding=1), nn.ReLU(),
                nn.Conv3d(8, 16, 3, stride=(1, 2, 2), padding=1), nn.ReLU(),
                nn.Conv3d(16, 24, 3, stride=(1, 2, 2), padding=1), nn.ReLU())
            self.head = nn.Linear(24 * history if head == "ordered-head" else 24, 5)

        def forward(self, frames):
            features = self.features(frames.permute(0, 2, 1, 3, 4))
            features = features.mean(dim=(3, 4)).flatten(1) if head == "ordered-head" else features.mean(dim=(2, 3, 4))
            output = self.head(features).sigmoid()
            width = 1.96 + output[:, 3:4] * (2000 - 1.96) if uncertainty == "gaussian-nll" else output[:, 3:4] * 2000
            return torch.cat((output[:, :2], output[:, 2:3] * 2000, width, output[:, 4:5]), dim=1)

    return TemporalReleaseModel()


def experiment_loss(output, target, mode="residual", epoch=0, warmup_epochs=5):
    if mode not in UNCERTAINTIES or epoch < 0 or warmup_epochs < 0:
        raise ValueError("Unsupported uncertainty loss configuration")
    import torch
    from torch.nn import functional as functional
    positive = target[:, 1]
    confidence = functional.binary_cross_entropy(output[:, :2], target[:, :2])
    phase = functional.smooth_l1_loss(output[:, 4], target[:, 4], reduction="none")
    if mode == "gaussian-nll":
        # Model width is an explicit 95% half-width; sigma is bounded 1 ms–~1 s.
        sigma = (output[:, 3] / 1960).clamp(0.001, 2000 / 1960)
        residual = (output[:, 2] - target[:, 2]) / 1000
        timing_uncertainty = .5 * (residual / sigma).square() + torch.log(sigma)
    else:
        timing = functional.smooth_l1_loss(output[:, 2] / 1000, target[:, 2] / 1000, reduction="none")
        residual = (output[:, 2].detach() - target[:, 2]).abs()
        uncertainty = functional.smooth_l1_loss(output[:, 3] / 1000, residual / 1000, reduction="none")
        if mode == "staged-residual" and epoch < warmup_epochs: uncertainty = uncertainty * 0
        timing_uncertainty = 4 * timing + uncertainty
    return confidence + ((timing_uncertainty + phase) * positive).sum() / positive.sum().clamp_min(1)
