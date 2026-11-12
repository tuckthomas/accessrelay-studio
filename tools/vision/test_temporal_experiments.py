"""Optional numerical/export tests; stdlib metrics/plans run without ML packages."""
import importlib.util
import math
from pathlib import Path
import sys
import tempfile
import unittest

TRAINING = Path(__file__).resolve().parents[2] / "games/nba2k27/training"
sys.path.insert(0, str(TRAINING))
from temporal_experiments import HEADS, UNCERTAINTIES, build_model, experiment_loss, uncertainty_semantics
from experiment_metrics import summarize
spec = importlib.util.spec_from_file_location("experiment_matrix", TRAINING / "Run-VisionExperiments.py")
matrix = importlib.util.module_from_spec(spec); spec.loader.exec_module(matrix)


class ExperimentPlanTests(unittest.TestCase):
    def test_matrix_pairs_every_head_loss_with_identical_seeds(self):
        jobs = matrix.job_specs(HEADS, UNCERTAINTIES, [123, 456])
        self.assertEqual(len(jobs), 12); self.assertEqual(len({job["name"] for job in jobs}), 12)
        for seed in (123, 456): self.assertEqual(sum(job["seed"] == seed for job in jobs), 6)
        for heads, modes, seeds in [(HEADS, UNCERTAINTIES, [1, 1]), (HEADS, UNCERTAINTIES, [-1]), (["future"], UNCERTAINTIES, [1]), (HEADS, ["unknown"], [1])]:
            with self.assertRaises(ValueError): matrix.job_specs(heads, modes, seeds)

    def test_metrics_distinguish_uncertainty_semantics_and_describe_cpu_cost(self):
        report = summarize([-2, 0, 4], [2, 2, 2], [.3, .1, .2], "gaussian-95-half-width-v1")
        self.assertEqual(report["timingMAEms"], 2); self.assertEqual(report["timingP99ms"], 4)
        self.assertEqual(report["coverageWithinPredictedUncertainty"], 2 / 3)
        self.assertEqual(report["coverageWithinOneSigma"], 1 / 3); self.assertEqual(report["cpuInferenceP50ms"], .2)
        self.assertNotIn("coverageWithinOneSigma", summarize([1], [2], [1], "absolute-error-estimate-v1"))
        for errors, widths, timing in [([math.nan], [1], [1]), ([1], [-1], [1]), ([1], [], [1])]:
            with self.assertRaises(ValueError): summarize(errors, widths, timing, "gaussian-95-half-width-v1")
        self.assertIsNone(summarize([], [], [], "absolute-error-estimate-v1")["timingMAEms"])

    def test_unknown_model_semantics_fail_before_optional_dependency_import(self):
        with self.assertRaises(ValueError): uncertainty_semantics("future")
        with self.assertRaises(ValueError): build_model(4, "unsupported")


@unittest.skipUnless(importlib.util.find_spec("torch"), "Optional offline PyTorch environment is not installed")
class TemporalNumericalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import torch
        cls.torch = torch; torch.set_num_threads(1)

    def test_all_heads_losses_have_bounded_outputs_and_finite_gradients(self):
        torch = self.torch
        for head in HEADS:
            for mode in UNCERTAINTIES:
                with self.subTest(head=head, mode=mode):
                    torch.manual_seed(123)
                    model = build_model(4, head, mode)
                    output = model(torch.rand(2, 4, 3, 32, 32))
                    self.assertEqual(tuple(output.shape), (2, 5)); self.assertTrue(torch.isfinite(output).all())
                    self.assertTrue((output[:, 2:4] >= 0).all() and (output[:, 2:4] <= 2000).all())
                    target = torch.tensor([[1., 1., 100., 0., .5], [0., 0., 0., 0., 0.]])
                    loss = experiment_loss(output, target, mode); loss.backward()
                    self.assertTrue(torch.isfinite(loss)); self.assertTrue(all(torch.isfinite(p.grad).all() for p in model.parameters()))

    def test_ordered_head_preserves_position_when_features_are_controlled(self):
        torch = self.torch
        class Features(torch.nn.Module):
            def forward(self, frames): return frames.repeat(1, 8, 1, 1, 1)
        frames = torch.zeros(1, 4, 3, 2, 2); frames[:, 0, 0] = 1
        values = {}
        for head in HEADS:
            model = build_model(4, head); model.features = Features()
            with torch.no_grad():
                model.head.weight.zero_(); model.head.bias.zero_(); model.head.weight[2, 0] = 1; model.head.weight[2, 1] = -1
                values[head] = (model(frames)[0, 2].item(), model(frames.flip(1))[0, 2].item())
        self.assertEqual(*values["global-pool"]); self.assertNotEqual(*values["ordered-head"])

    def test_warmup_freezes_uncertainty_target_and_gaussian_floor_is_finite(self):
        torch = self.torch
        target = torch.tensor([[1., 1., 100., 0., .5]])
        for epoch in (0, 5):
            output = torch.tensor([[.9, .9, 80., 100., .5]], requires_grad=True)
            experiment_loss(output, target, "staged-residual", epoch, 5).backward()
            self.assertEqual(output.grad[0, 3].item() == 0, epoch == 0)
        output = torch.tensor([[.9, .9, 80., 0., .5]], requires_grad=True)
        loss = experiment_loss(output, target, "gaussian-nll"); loss.backward()
        self.assertTrue(torch.isfinite(loss) and torch.isfinite(output.grad).all())

    def test_seeded_initialization_and_update_are_reproducible(self):
        torch = self.torch; results = []
        for _ in range(2):
            torch.manual_seed(123); model = build_model(4, "ordered-head", "gaussian-nll")
            optimizer = torch.optim.AdamW(model.parameters(), lr=.001)
            frames = torch.rand(2, 4, 3, 32, 32); target = torch.tensor([[1., 1., 100., 0., .5], [0., 0., 0., 0., 0.]])
            experiment_loss(model(frames), target, "gaussian-nll").backward(); optimizer.step()
            results.append({key: value.detach().clone() for key, value in model.state_dict().items()})
        self.assertTrue(all(torch.equal(value, results[1][key]) for key, value in results[0].items()))

    @unittest.skipUnless(importlib.util.find_spec("onnx") and importlib.util.find_spec("onnxruntime"), "Optional ONNX export environment is not installed")
    def test_all_variants_export_with_cpu_numerical_parity(self):
        import onnx
        import onnxruntime as ort
        import numpy as np
        torch = self.torch
        with tempfile.TemporaryDirectory(prefix="accessrelay-onnx-experiments-") as temporary:
            for head in HEADS:
                for mode in UNCERTAINTIES:
                    with self.subTest(head=head, mode=mode):
                        torch.manual_seed(123); model = build_model(4, head, mode).eval(); sample = torch.rand(1, 4, 3, 32, 32)
                        path = Path(temporary) / f"{head}-{mode}.onnx"
                        torch.onnx.export(model, (sample,), str(path), input_names=["frames"], output_names=["observation"], opset_version=17, dynamo=False, external_data=False)
                        onnx.checker.check_model(onnx.load(path))
                        options = ort.SessionOptions(); options.intra_op_num_threads = 1
                        session = ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])
                        actual = session.run(["observation"], {"frames": sample.numpy()})[0]
                        with torch.inference_mode(): expected = model(sample).numpy()
                        np.testing.assert_allclose(actual, expected, atol=.002, rtol=1e-5)


if __name__ == "__main__": unittest.main()
