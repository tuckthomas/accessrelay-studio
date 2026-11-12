-- Host owns output, cancellation and final vision arbitration.
output.axis("RightY", -100)
wait(tuning.get("down-hold-ms"))
output.axis("RightY", 100)
wait(tuning.get("up-hold-ms"))
output.clear()
