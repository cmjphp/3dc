Shader "Hidden/Cutting/GpuSdfWorkpiece"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.72, 0.76, 0.78, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float> _SdfSamples;
            struct VisualCutOperation
            {
                float4 startRadius;
                float4 endHeight;
                float4 axis;
                float4 profileMeta;
                float4 profile0;
                float4 profile1;
                float4 profile2;
                float4 profile3;
                float4 profile4;
                float4 profile5;
                float4 profile6;
                float4 profile7;
            };
            StructuredBuffer<VisualCutOperation> _VisualCutOperations;
            struct DetachedRemovalBox
            {
                float4 center;
                float4 size;
            };
            StructuredBuffer<DetachedRemovalBox> _DetachedRemovalBoxes;
            StructuredBuffer<float> _DetachedVoxelMask;
            float4x4 _LocalToWorld;
            float4x4 _WorldToLocal;
            float4 _BaseColor;
            float4 _LightDirection;
            float4 _SampleSize;
            float4 _GridMin;
            float4 _LocalSize;
            float4 _DisplaySize;
            float4 _DisplayCenter;
            float4 _DetachedVoxelMaskSize;
            float4 _DetachedVoxelMaskMin;
            float4 _DetachedVoxelMaskStep;
            float _VoxelSize;
            float _IsoLevel;
            float _MaxSteps;
            float _StepScale;
            float _DetachedVoxelMaskAirValue;
            int _VisualCutOperationCount;
            int _DetachedRemovalBoxCount;
            int _ProfileSegmentCount;
            int _AngularProfileAxialSampleCount;
            int _AngularProfileAngleSampleCount;
            float _AngularProfileMinRadiusSamples[512];
            float _AngularProfileMaxRadiusSamples[512];

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
            };

            struct FragmentOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            Varyings Vert(Attributes input)
            {
                float3 worldPosition = mul(_LocalToWorld, float4(input.positionOS, 1.0)).xyz;

                Varyings output;
                output.positionCS = TransformWorldToHClip(worldPosition);
                output.positionWS = worldPosition;
                output.positionOS = input.positionOS;
                return output;
            }

            int SampleIndex(int3 sample)
            {
                int3 sampleSize = int3(_SampleSize.xyz);
                return sample.x + sampleSize.x * (sample.y + sampleSize.y * sample.z);
            }

            float GetSdf(int3 sample)
            {
                int3 sampleSize = int3(_SampleSize.xyz);
                sample = clamp(sample, int3(0, 0, 0), sampleSize - 1);
                return _SdfSamples[SampleIndex(sample)];
            }

            float SampleDetailSdf(float3 localPoint)
            {
                int3 sampleSize = int3(_SampleSize.xyz);
                float3 grid = (localPoint - _GridMin.xyz) / _VoxelSize + 1.0;
                int3 i0 = clamp((int3)floor(grid), int3(0, 0, 0), sampleSize - 2);
                int3 i1 = min(i0 + 1, sampleSize - 1);
                float3 t = saturate(grid - float3(i0));

                float c000 = GetSdf(int3(i0.x, i0.y, i0.z));
                float c100 = GetSdf(int3(i1.x, i0.y, i0.z));
                float c010 = GetSdf(int3(i0.x, i1.y, i0.z));
                float c110 = GetSdf(int3(i1.x, i1.y, i0.z));
                float c001 = GetSdf(int3(i0.x, i0.y, i1.z));
                float c101 = GetSdf(int3(i1.x, i0.y, i1.z));
                float c011 = GetSdf(int3(i0.x, i1.y, i1.z));
                float c111 = GetSdf(int3(i1.x, i1.y, i1.z));

                float c00 = lerp(c000, c100, t.x);
                float c10 = lerp(c010, c110, t.x);
                float c01 = lerp(c001, c101, t.x);
                float c11 = lerp(c011, c111, t.x);
                float c0 = lerp(c00, c10, t.y);
                float c1 = lerp(c01, c11, t.y);
                return lerp(c0, c1, t.z);
            }

            float BoxSdf(float3 samplePosition, float3 boxCenter, float3 boxSize)
            {
                float3 voxelVector = float3(_VoxelSize, _VoxelSize, _VoxelSize);
                float3 halfSize = max(boxSize * 0.5, voxelVector * 0.5);
                float3 q = abs(samplePosition - boxCenter) - halfSize;
                return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
            }

            float GetVisualProfileSample(VisualCutOperation operation, int index)
            {
                if (index < 0 || index >= 32)
                {
                    return 0.0;
                }

                float4 packed = index < 4
                    ? operation.profile0
                    : index < 8
                        ? operation.profile1
                        : index < 12
                            ? operation.profile2
                            : index < 16
                                ? operation.profile3
                                : index < 20
                                    ? operation.profile4
                                    : index < 24
                                        ? operation.profile5
                                        : index < 28
                                            ? operation.profile6
                                            : operation.profile7;
                return packed[index & 3];
            }

            float VisualProfileRadius(float axial, float radius, float height, VisualCutOperation operation)
            {
                float normalizedHeight = saturate(axial / max(height, _VoxelSize * 0.000001));
                int sampleCount = clamp((int)round(operation.profileMeta.x), 0, 32);
                if (sampleCount >= 2)
                {
                    float samplePosition = normalizedHeight * (float)(sampleCount - 1);
                    int sampleIndex = min((int)floor(samplePosition), sampleCount - 2);
                    float t = saturate(samplePosition - sampleIndex);
                    float radiusA = GetVisualProfileSample(operation, sampleIndex);
                    float radiusB = GetVisualProfileSample(operation, sampleIndex + 1);
                    return radius * lerp(radiusA, radiusB, smoothstep(0.0, 1.0, t));
                }

                int segmentCount = max(_ProfileSegmentCount, 2);
                float profilePosition = normalizedHeight * segmentCount;
                int segment = min((int)floor(profilePosition), segmentCount - 1);
                float t = saturate(profilePosition - segment);
                float innerRadius = radius * 0.6535898;
                float radiusA = (segment & 1) == 0 ? radius : innerRadius;
                float radiusB = ((segment + 1) & 1) == 0 ? radius : innerRadius;
                return lerp(radiusA, radiusB, smoothstep(0.0, 1.0, t));
            }

            float VisualAngularProfileSample(float normalizedHeight, float normalizedAngle, bool sampleMin)
            {
                int axialCount = clamp(_AngularProfileAxialSampleCount, 0, 16);
                int angleCount = clamp(_AngularProfileAngleSampleCount, 0, 32);
                if (axialCount < 2 || angleCount < 3)
                {
                    return -1.0;
                }

                float axialPosition = saturate(normalizedHeight) * (float)(axialCount - 1);
                int axial0 = min((int)floor(axialPosition), axialCount - 2);
                int axial1 = axial0 + 1;
                float axialT = saturate(axialPosition - axial0);
                float anglePosition = frac(normalizedAngle) * (float)angleCount;
                int angle0 = (int)floor(anglePosition) % angleCount;
                int angle1 = (angle0 + 1) % angleCount;
                float angleT = saturate(anglePosition - floor(anglePosition));
                int index00 = axial0 * angleCount + angle0;
                int index01 = axial0 * angleCount + angle1;
                int index10 = axial1 * angleCount + angle0;
                int index11 = axial1 * angleCount + angle1;
                float value00 = sampleMin ? _AngularProfileMinRadiusSamples[index00] : _AngularProfileMaxRadiusSamples[index00];
                float value01 = sampleMin ? _AngularProfileMinRadiusSamples[index01] : _AngularProfileMaxRadiusSamples[index01];
                float value10 = sampleMin ? _AngularProfileMinRadiusSamples[index10] : _AngularProfileMaxRadiusSamples[index10];
                float value11 = sampleMin ? _AngularProfileMinRadiusSamples[index11] : _AngularProfileMaxRadiusSamples[index11];
                float angleA = lerp(value00, value01, smoothstep(0.0, 1.0, angleT));
                float angleB = lerp(value10, value11, smoothstep(0.0, 1.0, angleT));
                return lerp(angleA, angleB, smoothstep(0.0, 1.0, axialT));
            }

            float2 VisualAngularProfileInterval(float axial, float3 radial, float3 axis, float radius, float height, VisualCutOperation operation)
            {
                int axialCount = clamp(_AngularProfileAxialSampleCount, 0, 16);
                int angleCount = clamp(_AngularProfileAngleSampleCount, 0, 32);
                if (axialCount < 2 || angleCount < 3)
                {
                    return float2(-1.0, -1.0);
                }

                float safeHeight = max(height, _VoxelSize * 0.000001);
                float normalizedHeight = saturate(axial / safeHeight);
                float3 right = operation.profileMeta.yzw - axis * dot(operation.profileMeta.yzw, axis);
                float rightLength = length(right);
                right = rightLength > 0.000001 ? right / rightLength : normalize(abs(axis.y) < 0.99 ? cross(axis, float3(0, 1, 0)) : cross(axis, float3(1, 0, 0)));
                float3 forward = normalize(cross(axis, right));
                float angle = atan2(dot(radial, forward), dot(radial, right));
                float normalizedAngle = frac(angle / 6.28318530718 + 1.0);
                float minRadius = VisualAngularProfileSample(normalizedHeight, normalizedAngle, true);
                float maxRadius = VisualAngularProfileSample(normalizedHeight, normalizedAngle, false);
                if (maxRadius <= 0.000001 || minRadius < 0.0)
                {
                    return float2(-1.0, -1.0);
                }

                return float2(radius * minRadius, radius * max(maxRadius, minRadius));
            }

            float VisualProfileDifferenceAtRoot(
                float3 samplePosition,
                float3 cutterRoot,
                float3 axis,
                float radius,
                float height,
                VisualCutOperation operation)
            {
                float3 toSample = samplePosition - cutterRoot;
                float axial = dot(toSample, axis);
                float3 radial = toSample - axis * axial;
                float radialDistance = length(radial);
                float2 interval = VisualAngularProfileInterval(axial, radial, axis, radius, height, operation);
                bool usesAngularProfile = _AngularProfileAxialSampleCount >= 2 && _AngularProfileAngleSampleCount >= 3;
                float radialDifference = usesAngularProfile
                    ? (interval.y >= 0.0 ? interval.y - radialDistance : -1000000.0)
                    : VisualProfileRadius(axial, radius, height, operation) - radialDistance;
                float axialDifference = min(axial, height - axial);
                return min(radialDifference, axialDifference);
            }

            float VisualProfileMaxRadius(float minAxial, float maxAxial, float radius, float height, VisualCutOperation operation)
            {
                float safeHeight = max(height, _VoxelSize * 0.000001);
                float clampedMin = clamp(minAxial, 0.0, safeHeight);
                float clampedMax = clamp(maxAxial, 0.0, safeHeight);
                int sampleCount = clamp((int)round(operation.profileMeta.x), 0, 32);
                if (sampleCount >= 2)
                {
                    float minNorm = saturate(clampedMin / safeHeight);
                    float maxNorm = saturate(clampedMax / safeHeight);
                    float minSamplePosition = minNorm * (float)(sampleCount - 1);
                    float maxSamplePosition = maxNorm * (float)(sampleCount - 1);
                    float maxRadius = max(
                        VisualProfileRadius(clampedMin, radius, height, operation),
                        VisualProfileRadius(clampedMax, radius, height, operation));

                    for (int i = 0; i < 32; i++)
                    {
                        if (i < sampleCount && (float)i >= minSamplePosition && (float)i <= maxSamplePosition)
                        {
                            maxRadius = max(maxRadius, radius * GetVisualProfileSample(operation, i));
                        }
                    }

                    return maxRadius;
                }

                float maxRadius = max(
                    VisualProfileRadius(clampedMin, radius, height, operation),
                    VisualProfileRadius(clampedMax, radius, height, operation));
                int segmentCount = max(_ProfileSegmentCount, 2);
                float minProfilePosition = clampedMin / safeHeight * segmentCount;
                float maxProfilePosition = clampedMax / safeHeight * segmentCount;
                int firstEvenBoundary = (int)ceil(minProfilePosition);
                if ((firstEvenBoundary & 1) != 0)
                {
                    firstEvenBoundary++;
                }

                if ((float)firstEvenBoundary <= maxProfilePosition)
                {
                    maxRadius = radius;
                }

                return maxRadius;
            }

            float VisualAxialSweepDifference(
                float3 samplePosition,
                float3 start,
                float3 end,
                float3 axis,
                float radius,
                float height,
                VisualCutOperation operation)
            {
                float3 motion = end - start;
                float travel = dot(motion, axis);
                float3 fromStart = samplePosition - start;
                float sampleAxial = dot(fromStart, axis);
                float axialAtEnd = sampleAxial - travel;
                float profileMin = max(0.0, min(sampleAxial, axialAtEnd));
                float profileMax = min(height, max(sampleAxial, axialAtEnd));

                if (profileMin > profileMax)
                {
                    return max(
                        VisualProfileDifferenceAtRoot(samplePosition, start, axis, radius, height, operation),
                        VisualProfileDifferenceAtRoot(samplePosition, end, axis, radius, height, operation));
                }

                float3 radial = fromStart - axis * sampleAxial;
                float radialDifference = VisualProfileMaxRadius(
                    profileMin,
                    profileMax,
                    radius,
                    height,
                    operation) - length(radial);
                float sweepMin = min(0.0, travel);
                float sweepMax = max(height, height + travel);
                float axialDifference = min(sampleAxial - sweepMin, sweepMax - sampleAxial);
                return min(radialDifference, axialDifference);
            }

            float VisualCutDifference(float3 samplePosition, VisualCutOperation operation)
            {
                float3 start = operation.startRadius.xyz;
                float3 end = operation.endHeight.xyz;
                float radius = operation.startRadius.w;
                float height = operation.endHeight.w;
                float3 axis = normalize(operation.axis.xyz);
                float3 startTop = start + axis * height;
                float3 endTop = end + axis * height;
                float3 boundsMin = min(min(start, end), min(startTop, endTop)) - radius;
                float3 boundsMax = max(max(start, end), max(startTop, endTop)) + radius;
                if (any(samplePosition < boundsMin) || any(samplePosition > boundsMax))
                {
                    return -1000000.0;
                }

                float3 motion = end - start;
                float3 radialMotion = motion - axis * dot(motion, axis);
                float3 toSampleFromStart = samplePosition - start;
                float3 radialFromStart = toSampleFromStart - axis * dot(toSampleFromStart, axis);
                float t = 1.0;
                float radialMotionLengthSqr = dot(radialMotion, radialMotion);
                float geometryScale = max(max(radius, height), _VoxelSize);
                float radialMotionEpsilon = geometryScale * 0.000001;

                bool usesAngularProfile = _AngularProfileAxialSampleCount >= 2 && _AngularProfileAngleSampleCount >= 3;
                if (radialMotionLengthSqr <= radialMotionEpsilon * radialMotionEpsilon && !usesAngularProfile)
                {
                    return VisualAxialSweepDifference(
                        samplePosition,
                        start,
                        end,
                        axis,
                        radius,
                        height,
                        operation);
                }
                else
                {
                    t = saturate(dot(radialFromStart, radialMotion) / radialMotionLengthSqr);
                }

                float projectedDifference = VisualProfileDifferenceAtRoot(
                    samplePosition,
                    lerp(start, end, t),
                    axis,
                    radius,
                    height,
                    operation);
                float endDifference = VisualProfileDifferenceAtRoot(
                    samplePosition,
                    end,
                    axis,
                    radius,
                    height,
                    operation);
                return max(projectedDifference, endDifference);
            }

            float SampleVisualCuts(float3 localPoint)
            {
                float cutDifference = -1000000.0;
                [loop]
                for (int operationIndex = 0; operationIndex < _VisualCutOperationCount; operationIndex++)
                {
                    cutDifference = max(cutDifference, VisualCutDifference(localPoint, _VisualCutOperations[operationIndex]));
                }
                return cutDifference;
            }

            float SampleDetachedRemovals(float3 localPoint)
            {
                float removalDifference = -1000000.0;
                [loop]
                for (int boxIndex = 0; boxIndex < _DetachedRemovalBoxCount; boxIndex++)
                {
                    DetachedRemovalBox removal = _DetachedRemovalBoxes[boxIndex];
                    removalDifference = max(
                        removalDifference,
                        -BoxSdf(localPoint, removal.center.xyz, removal.size.xyz));
                }
                return removalDifference;
            }

            float SampleDetachedVoxelMask(float3 localPoint)
            {
                int3 maskSize = int3(_DetachedVoxelMaskSize.xyz);
                if (any(maskSize <= 0))
                {
                    return -1000000.0;
                }

                float3 safeStep = max(
                    _DetachedVoxelMaskStep.xyz,
                    float3(0.000001, 0.000001, 0.000001));
                float3 normalized = (localPoint - _DetachedVoxelMaskMin.xyz) / safeStep;
                if (any(normalized < 0.0) || any(normalized >= float3(maskSize)))
                {
                    return -1000000.0;
                }

                float3 grid = normalized - 0.5;
                int3 i0 = clamp((int3)floor(grid), int3(0, 0, 0), maskSize - 1);
                int3 i1 = min(i0 + 1, maskSize - 1);
                float3 t = saturate(grid - float3(i0));

                int i000 = i0.x + maskSize.x * (i0.y + maskSize.y * i0.z);
                int i100 = i1.x + maskSize.x * (i0.y + maskSize.y * i0.z);
                int i010 = i0.x + maskSize.x * (i1.y + maskSize.y * i0.z);
                int i110 = i1.x + maskSize.x * (i1.y + maskSize.y * i0.z);
                int i001 = i0.x + maskSize.x * (i0.y + maskSize.y * i1.z);
                int i101 = i1.x + maskSize.x * (i0.y + maskSize.y * i1.z);
                int i011 = i0.x + maskSize.x * (i1.y + maskSize.y * i1.z);
                int i111 = i1.x + maskSize.x * (i1.y + maskSize.y * i1.z);

                float c00 = lerp(_DetachedVoxelMask[i000], _DetachedVoxelMask[i100], t.x);
                float c10 = lerp(_DetachedVoxelMask[i010], _DetachedVoxelMask[i110], t.x);
                float c01 = lerp(_DetachedVoxelMask[i001], _DetachedVoxelMask[i101], t.x);
                float c11 = lerp(_DetachedVoxelMask[i011], _DetachedVoxelMask[i111], t.x);
                float c0 = lerp(c00, c10, t.y);
                float c1 = lerp(c01, c11, t.y);
                return lerp(c0, c1, t.z);
            }

            float SampleSdf(float3 localPoint)
            {
                float fullWorkpiece = BoxSdf(localPoint, _DisplayCenter.xyz, _DisplaySize.xyz);
                float combinedWorkpiece = max(
                    max(
                        max(fullWorkpiece, SampleVisualCuts(localPoint)),
                        SampleDetachedRemovals(localPoint)),
                    SampleDetachedVoxelMask(localPoint));
                float3 detailHalfSize = _LocalSize.xyz * 0.5;
                float3 voxelVector = float3(_VoxelSize, _VoxelSize, _VoxelSize);

                if (any(abs(localPoint) > detailHalfSize + voxelVector))
                {
                    return combinedWorkpiece;
                }

                float storedDetail = SampleDetailSdf(localPoint);
                float3 initialHalfSize = max(detailHalfSize - voxelVector * 0.5, voxelVector * 0.5);
                float3 q = abs(localPoint) - initialHalfSize;
                float initialDetail = length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
                float cutValue = storedDetail > initialDetail + _VoxelSize * 0.0001
                    ? storedDetail
                    : -1000000.0;
                return max(combinedWorkpiece, cutValue);
            }

            float3 GetSdfNormal(float3 localPoint)
            {
                float e = _VoxelSize;
                float detachedMaskValue = SampleDetachedVoxelMask(localPoint);
                float detachedMaskStep = min(
                    _DetachedVoxelMaskStep.x,
                    min(_DetachedVoxelMaskStep.y, _DetachedVoxelMaskStep.z));
                if (detachedMaskValue > -999999.0 &&
                    abs(detachedMaskValue) <= detachedMaskStep * 2.0)
                {
                    // The global connectivity grid is intentionally coarser
                    // than the local cutting grid. Sample its normal over a
                    // matching footprint instead of magnifying cell corners.
                    e = max(e, detachedMaskStep * 0.3);
                }
                float dx = SampleSdf(localPoint + float3(e, 0.0, 0.0)) - SampleSdf(localPoint - float3(e, 0.0, 0.0));
                float dy = SampleSdf(localPoint + float3(0.0, e, 0.0)) - SampleSdf(localPoint - float3(0.0, e, 0.0));
                float dz = SampleSdf(localPoint + float3(0.0, 0.0, e)) - SampleSdf(localPoint - float3(0.0, 0.0, e));
                float3 normal = float3(dx, dy, dz);
                return dot(normal, normal) < 0.000001 ? float3(0.0, 1.0, 0.0) : normalize(normal);
            }

            bool IntersectBox(float3 origin, float3 direction, float3 boxMin, float3 boxMax, out float tMin, out float tMax)
            {
                float3 safeDirection = direction;
                safeDirection.x = abs(safeDirection.x) < 0.00001 ? (safeDirection.x < 0.0 ? -0.00001 : 0.00001) : safeDirection.x;
                safeDirection.y = abs(safeDirection.y) < 0.00001 ? (safeDirection.y < 0.0 ? -0.00001 : 0.00001) : safeDirection.y;
                safeDirection.z = abs(safeDirection.z) < 0.00001 ? (safeDirection.z < 0.0 ? -0.00001 : 0.00001) : safeDirection.z;

                float3 t0 = (boxMin - origin) / safeDirection;
                float3 t1 = (boxMax - origin) / safeDirection;
                float3 nearT = min(t0, t1);
                float3 farT = max(t0, t1);

                tMin = max(max(nearT.x, nearT.y), nearT.z);
                tMax = min(min(farT.x, farT.y), farT.z);
                return tMax >= max(tMin, 0.0);
            }

            FragmentOutput Frag(Varyings input)
            {
                float3 cameraLocal = mul(_WorldToLocal, float4(_WorldSpaceCameraPos.xyz, 1.0)).xyz;
                float3 rayDirection = normalize(input.positionOS - cameraLocal);
                float3 halfSize = _DisplaySize.xyz * 0.5;
                float3 boxMin = _DisplayCenter.xyz - halfSize;
                float3 boxMax = _DisplayCenter.xyz + halfSize;
                float tMin;
                float tMax;

                if (!IntersectBox(cameraLocal, rayDirection, boxMin, boxMax, tMin, tMax))
                {
                    discard;
                }

                float t = max(tMin, 0.0);
                float lastT = t;
                float lastValue = SampleSdf(cameraLocal + rayDirection * t) - _IsoLevel;
                float hitT = -1.0;
                float minStep = max(_VoxelSize * _StepScale * 0.35, 0.0005);
                float hitEpsilon = max(_VoxelSize * 0.08, 0.0005);
                int maxSteps = max(16, (int)_MaxSteps);

                [loop]
                for (int i = 0; i < maxSteps && t <= tMax; i++)
                {
                    float3 p = cameraLocal + rayDirection * t;
                    float value = SampleSdf(p) - _IsoLevel;

                    if (value <= hitEpsilon)
                    {
                        hitT = t;

                        if (lastValue > 0.0 && value <= 0.0)
                        {
                            float low = lastT;
                            float high = t;

                            [unroll]
                            for (int j = 0; j < 8; j++)
                            {
                                float mid = (low + high) * 0.5;
                                float midValue = SampleSdf(cameraLocal + rayDirection * mid) - _IsoLevel;
                                if (midValue > 0.0)
                                {
                                    low = mid;
                                }
                                else
                                {
                                    high = mid;
                                }
                            }

                            hitT = high;
                        }

                        break;
                    }

                    lastT = t;
                    lastValue = value;
                    t += max(abs(value) * _StepScale, minStep);
                }

                if (hitT < 0.0)
                {
                    discard;
                }

                float3 hitLocal = cameraLocal + rayDirection * hitT;
                float3 hitWorld = mul(_LocalToWorld, float4(hitLocal, 1.0)).xyz;
                float3 normalLocal = GetSdfNormal(hitLocal);
                float3 normalWorld = normalize(mul((float3x3)_LocalToWorld, normalLocal));
                float3 lightDirection = normalize(_LightDirection.xyz);
                float diffuse = saturate(dot(normalWorld, lightDirection));
                float rim = pow(1.0 - saturate(normalWorld.y * 0.5 + 0.5), 2.0) * 0.08;
                float lighting = 0.26 + diffuse * 0.74 + rim;
                float4 clipPosition = TransformWorldToHClip(hitWorld);

                FragmentOutput output;
                output.color = half4(_BaseColor.rgb * lighting, _BaseColor.a);
                output.depth = clipPosition.z / clipPosition.w;
                return output;
            }
            ENDHLSL
        }
    }
}
