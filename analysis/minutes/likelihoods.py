"""Predictive log scores in nats, using minutes as the observation unit.

Only models with an explicit predictive distribution receive LL. Point-only
models remain unscored, rather than receiving an invented residual variance.
"""

import numpy as np


def gaussian_log_scores(rows, predictions, predictive_variances, latent_variances=None, split=None):
    indices, seen, duplicates = [], {}, {}
    for i, (row, prediction) in enumerate(zip(rows, predictions)):
        o = row["Observation"]
        variance = predictive_variances[i]
        if (split is None or o["Split"] == split) and prediction is not None and o["RegulationSeconds"] is not None and variance is not None:
            if not np.isfinite(prediction) or not np.isfinite(variance) or variance <= 0:
                raise ValueError("Non-finite prediction or nonpositive predictive variance")
            indices.append(i)
            key = (o["RegistryId"], o["PlayerId"], o["Started"], o["Date"])
            if key in seen:
                duplicates.setdefault(key, [seen[key]]).append(i)
            else:
                seen[key] = i
    count = len(indices)
    actual = np.array([rows[i]["Observation"]["RegulationSeconds"]/60 for i in indices])
    means = np.array([predictions[i] for i in indices])
    variances = np.array([predictive_variances[i] for i in indices])
    total = float(np.sum(-.5*(np.log(2*np.pi*variances)+(actual-means)**2/variances)))
    if latent_variances is not None:
        latent = np.array([latent_variances[i] for i in indices], dtype=float)
        if np.any(~np.isfinite(latent)) or np.any(latent < 0) or np.any(variances-latent <= 0):
            raise ValueError("Invalid latent/observation variance decomposition")
        # Most player-dates are singletons; correct only the rare joint same-day groups.
        for group in duplicates.values():
            first, n = group[0], len(group)
            actual = np.array([rows[i]["Observation"]["RegulationSeconds"]/60 for i in group])
            mean, variance = predictions[first], predictive_variances[first]
            if any(not np.isclose(predictions[i], mean) or not np.isclose(predictive_variances[i], variance) for i in group):
                raise ValueError("Same-date Gaussian predictions must use the same pre-update state")
            v = latent_variances[first]
            if any(not np.isclose(latent_variances[i], v) for i in group):
                raise ValueError("Same-date observations must share the same latent variance")
            noise, denominator = variance-v, v+(variance-v)/n
            independent = -.5*np.sum(np.log(2*np.pi*variance)+(actual-mean)**2/variance)
            joint = -.5*(np.log(denominator)+np.log(n)+(n-1)*np.log(noise)
                          +(actual.mean()-mean)**2/denominator+np.sum((actual-actual.mean())**2)/noise+n*np.log(2*np.pi))
            total += joint-independent
    return {"LogLikelihoodSum": float(total) if count else None,
            "MeanLogLikelihood": float(total/count) if count else None, "LogLikelihoodN": count}


def enrich_report_scores(payload):
    if payload.get("Summary", {}).get("VersionRegistry"):
        return payload
    models = []
    for model in payload.get("Models", []):
        if "PredictiveVarianceMinutesSquared" in model:
            model = dict(model)
            for split in ("Training", "Validation"):
                model[split] = {**model[split], **gaussian_log_scores(payload["Rows"], model["PredictionsMinutes"],
                    model["PredictiveVarianceMinutesSquared"], model.get("LatentVarianceMinutesSquared"), split)}
        models.append(model)
    return {**payload, "Models": models} if "Models" in payload else payload
