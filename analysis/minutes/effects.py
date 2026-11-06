"""Interchangeable uncertainty-dynamics effects for the shared Gaussian filter.

Add an implementation here; the filter, fitting and study runner remain unchanged.
"""

from abc import ABC, abstractmethod

import numpy as np


class UncertaintyDynamics(ABC):
    key: str
    raw_names: tuple
    bounds: tuple

    @abstractmethod
    def decode(self, raw, initial_variance):
        """Return named physical parameters for fitting output and filtering."""

    @abstractmethod
    def propagate(self, variance, elapsed_days, parameters):
        """Prior latent variance from posterior variance and elapsed time."""

    @abstractmethod
    def propagate_gradient(self, variance, derivative, days, initial, raw, offset):
        """Same propagation plus derivatives with respect to the full raw vector."""

    @abstractmethod
    def starting_raw(self, index):
        """Deterministic effect-parameter start for a common multistart fit."""


class CalendarDriftFittedCap(UncertaintyDynamics):
    key = "calendar_drift_fitted_cap"
    raw_names = ("LogDailyDriftVariance", "LogCapExtraVariance")
    bounds = ((-18., 8.), (-12., 12.))

    def decode(self, raw, initial_variance):
        return {"DailyDriftVariance": float(np.exp(raw[0])),
                "VarianceCapMinutesSquared": float(initial_variance+np.exp(raw[1]))}

    def propagate(self, variance, elapsed_days, parameters):
        return np.minimum(parameters["VarianceCapMinutesSquared"], variance+parameters["DailyDriftVariance"]*elapsed_days)

    def propagate_gradient(self, variance, derivative, days, initial, raw, offset):
        drift, extra = np.exp(raw)
        cap = initial+extra
        proposed = variance+drift*days
        result = derivative.copy()
        result[:, offset] += drift*days
        dcap = np.zeros(result.shape[1])
        dcap[1], dcap[offset+1] = initial, extra
        return np.minimum(proposed, cap), np.where((proposed < cap)[:, None], result, dcap)

    def starting_raw(self, index):
        starts = [(0.1, 50.), (.5, 100.), (2., 150.), (.02, 20.), (1., 10.), (.3, 300.)]
        return np.log(starts[index % len(starts)])


DYNAMICS = {CalendarDriftFittedCap.key: CalendarDriftFittedCap}


def get_dynamics(key):
    if not isinstance(key, str) or key not in DYNAMICS:
        raise ValueError(f"Unknown uncertainty-dynamics implementation: {key}; available: {list(DYNAMICS)}")
    return DYNAMICS[key]()
