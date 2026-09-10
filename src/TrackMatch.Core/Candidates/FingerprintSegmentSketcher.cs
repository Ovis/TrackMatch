using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// raw Chromaprint Fingerprintを一定長のSimHash区間へ分割する。
/// </summary>
public sealed class FingerprintSegmentSketcher
{
    public IReadOnlyList<FingerprintSegmentSketch> Create(
        StoredFingerprint storedFingerprint,
        CandidateGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(storedFingerprint);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var values = storedFingerprint.Fingerprint.Values;
        if (values.Count == 0)
        {
            return [];
        }

        var length = Math.Min(options.SegmentLengthItems, values.Count);
        if (values.Count <= length)
        {
            return [new FingerprintSegmentSketch(storedFingerprint.TrackId, 0, ChromaprintSimHash.Compute(values, 0, length))];
        }

        var sketches = new List<FingerprintSegmentSketch>();
        var segmentIndex = 0;
        var lastStart = -1;
        for (var start = 0; start + length <= values.Count; start += options.SegmentStrideItems)
        {
            sketches.Add(new FingerprintSegmentSketch(
                storedFingerprint.TrackId,
                segmentIndex++,
                ChromaprintSimHash.Compute(values, start, length)));
            lastStart = start;
        }

        // strideで末尾まで割り切れない場合も、曲末側の区間を必ず索引へ含める。
        var tailStart = values.Count - length;
        if (tailStart != lastStart)
        {
            sketches.Add(new FingerprintSegmentSketch(
                storedFingerprint.TrackId,
                segmentIndex,
                ChromaprintSimHash.Compute(values, tailStart, length)));
        }

        return sketches;
    }
}
