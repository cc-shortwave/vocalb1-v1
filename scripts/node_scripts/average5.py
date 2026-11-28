"""VocalB1 external node sample: average of the five inputs.

The sandbox calls compute(values) once per synthesis frame. Only pure math is allowed:
no imports, no file or network access, and the return value must be one finite number.
"""


def compute(values):
    return sum(values) / len(values)
