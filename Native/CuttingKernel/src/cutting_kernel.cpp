// Native C/C++ SDF cutting kernel for Unity.
// Unity owns interaction and rendering; this file owns concrete SDF cutting.

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <exception>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(SDF_USE_OPENVDB)
#include <openvdb/openvdb.h>
#include <openvdb/tools/Interpolation.h>
#include <openvdb/tools/MeshToVolume.h>
#endif

#if defined(_WIN32)
#define SDF_EXPORT extern "C" __declspec(dllexport)
#else
#define SDF_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

struct Vec3 {
    float x;
    float y;
    float z;

    Vec3() : x(0.0f), y(0.0f), z(0.0f) {}
    Vec3(float px, float py, float pz) : x(px), y(py), z(pz) {}

    Vec3 operator+(const Vec3& rhs) const { return Vec3(x + rhs.x, y + rhs.y, z + rhs.z); }
    Vec3 operator-(const Vec3& rhs) const { return Vec3(x - rhs.x, y - rhs.y, z - rhs.z); }
    Vec3 operator*(float s) const { return Vec3(x * s, y * s, z * s); }
};

struct MeshCutterGeometry {
    std::vector<Vec3> vertices;
    std::vector<int> triangleIndices;
    Vec3 boundsMin;
    Vec3 boundsMax;
    float shellRadius = 0.0f;
};

enum CutOperationKind {
    CutOperationMesh = 0,
    CutOperationProfile = 1
};

enum BlankShape {
    BlankShapeBox = 0,
    BlankShapeCylinder = 1,
    BlankShapeTube = 2,
    BlankShapeHalfTube = 3,
    BlankShapeImportedMesh = 4
};

enum ComputeBackend {
    ComputeBackendCpu = 0,
    ComputeBackendGpu = 1
};

enum ComputeBackendCapabilities {
    ComputeBackendCapabilityCpu = 1 << 0,
    ComputeBackendCapabilityGpu = 1 << 1
};

struct SampleBounds {
    int minX = 0;
    int maxX = -1;
    int minY = 0;
    int maxY = -1;
    int minZ = 0;
    int maxZ = -1;
};

struct CapsuleCutRequest {
    Vec3 start;
    Vec3 end;
    float radius = 0.0f;
    SampleBounds bounds;
};

struct ProfileCutRequest {
    Vec3 start;
    Vec3 end;
    Vec3 axis;
    float radius = 0.0f;
    float height = 0.0f;
    float cutBand = 0.0f;
    SampleBounds bounds;
};

struct MeshCutRequest {
    std::shared_ptr<const MeshCutterGeometry> cutter;
    Vec3 start;
    Vec3 end;
    Vec3 axis;
    Vec3 right;
    float cutBand = 0.0f;
    SampleBounds bounds;
};

struct CutOperation {
    CutOperationKind kind = CutOperationMesh;
    Vec3 start;
    Vec3 end;
    Vec3 axis;
    Vec3 right;
    Vec3 boundsMin;
    Vec3 boundsMax;
    float cutBand = 0.0f;
    float radius = 0.0f;
    float height = 0.0f;
    int profileSegmentCount = 24;
    std::vector<float> profileRadiusSamples;
    std::shared_ptr<const MeshCutterGeometry> meshGeometry;
#if defined(SDF_USE_OPENVDB)
    openvdb::FloatGrid::Ptr cutterGrid;
#endif
};

struct State {
    bool initialized = false;
    int width = 0;
    int height = 0;
    int depth = 0;
    int sampleW = 0;
    int sampleH = 0;
    int sampleD = 0;
    float voxelSize = 0.0f;
    Vec3 gridMin;
    Vec3 localSize;
    int blankShape = BlankShapeBox;
    float blankInnerRadius = 0.0f;
    int preferredComputeBackend = ComputeBackendCpu;
    int activeComputeBackend = ComputeBackendCpu;
    int profileSegmentCount = 24;
    std::vector<float> profileRadiusSamples;
    bool connectivityReady = false;
    bool connectivityInProgress = false;
    bool islandsFound = false;
    bool removalApplied = false;
    int pendingRemovalCount = 0;
    int lastConnectivityComponentCount = 0;
    int lastConnectivityKeepCoreCount = 0;
    int lastConnectivityRemovalCandidateCount = 0;
    uint64_t materialRevision = 1;
    float connectivityRemovalThreshold = 0.02f;
    int64_t connectivityCheckIntervalMs = 200;
    std::chrono::steady_clock::time_point lastCutTime;
    std::vector<float> sdf;
    std::vector<int> pendingRemovalIndices;
    std::shared_ptr<MeshCutterGeometry> blankMesh;
    std::shared_ptr<MeshCutterGeometry> cutterMesh;
    std::vector<CutOperation> cutHistory;
#if defined(SDF_USE_OPENVDB)
    openvdb::FloatGrid::Ptr workpieceGrid;
    openvdb::FloatGrid::Ptr cutterGrid;
#endif
};

struct ImportedMeshData {
    std::vector<Vec3> vertices;
    std::vector<int> triangleIndices;
    Vec3 boundsMin;
    Vec3 boundsMax;
};

struct ImportedStlCache {
    std::string path;
    float scaleX = 0.0f;
    float scaleY = 0.0f;
    float scaleZ = 0.0f;
    ImportedMeshData mesh;
    bool valid = false;
};

State gState;
std::mutex gMutex;
std::mutex gImportCacheMutex;
std::thread gConnectivityThread;
ImportedStlCache gImportedStlCache;

float dot(const Vec3& a, const Vec3& b)
{
    return a.x * b.x + a.y * b.y + a.z * b.z;
}

float lengthSqr(const Vec3& v)
{
    return dot(v, v);
}

float length(const Vec3& v)
{
    return std::sqrt(lengthSqr(v));
}

Vec3 cross(const Vec3& a, const Vec3& b)
{
    return Vec3(
        a.y * b.z - a.z * b.y,
        a.z * b.x - a.x * b.z,
        a.x * b.y - a.y * b.x);
}

Vec3 normalize(const Vec3& v)
{
    float len = length(v);
    return len > 1.0e-6f ? v * (1.0f / len) : Vec3(0.0f, 1.0f, 0.0f);
}

float clamp01(float v)
{
    return std::min(1.0f, std::max(0.0f, v));
}

float clampF(float v, float lo, float hi)
{
    return std::min(hi, std::max(lo, v));
}

int clampI(int v, int lo, int hi)
{
    return std::min(hi, std::max(lo, v));
}

uint32_t readU32Le(const unsigned char* bytes)
{
    return static_cast<uint32_t>(bytes[0]) |
           (static_cast<uint32_t>(bytes[1]) << 8) |
           (static_cast<uint32_t>(bytes[2]) << 16) |
           (static_cast<uint32_t>(bytes[3]) << 24);
}

float readF32Le(const unsigned char* bytes)
{
    uint32_t raw = readU32Le(bytes);
    float value = 0.0f;
    std::memcpy(&value, &raw, sizeof(float));
    return value;
}

Vec3 sanitizeImportedScalePercent(float x, float y, float z)
{
    return Vec3(
        clampF(std::isfinite(x) ? x : 100.0f, 0.001f, 1000.0f),
        clampF(std::isfinite(y) ? y : 100.0f, 0.001f, 1000.0f),
        clampF(std::isfinite(z) ? z : 100.0f, 0.001f, 1000.0f));
}

void recalculateMeshBounds(ImportedMeshData& mesh)
{
    if (mesh.vertices.empty()) {
        mesh.boundsMin = Vec3();
        mesh.boundsMax = Vec3();
        return;
    }

    Vec3 min = mesh.vertices[0];
    Vec3 max = min;
    for (const Vec3& vertex : mesh.vertices) {
        min.x = std::min(min.x, vertex.x);
        min.y = std::min(min.y, vertex.y);
        min.z = std::min(min.z, vertex.z);
        max.x = std::max(max.x, vertex.x);
        max.y = std::max(max.y, vertex.y);
        max.z = std::max(max.z, vertex.z);
    }

    mesh.boundsMin = min;
    mesh.boundsMax = max;
}

void centerAndScaleImportedMesh(ImportedMeshData& mesh, const Vec3& scalePercent)
{
    recalculateMeshBounds(mesh);
    Vec3 center = (mesh.boundsMin + mesh.boundsMax) * 0.5f;
    Vec3 scale(scalePercent.x * 0.01f, scalePercent.y * 0.01f, scalePercent.z * 0.01f);
    for (Vec3& vertex : mesh.vertices) {
        vertex.x = (vertex.x - center.x) * scale.x;
        vertex.y = (vertex.y - center.y) * scale.y;
        vertex.z = (vertex.z - center.z) * scale.z;
    }

    recalculateMeshBounds(mesh);
}

bool readBinaryStl(const std::string& path, uint32_t triangleCount, ImportedMeshData& mesh)
{
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        return false;
    }

    file.seekg(84, std::ios::beg);
    std::array<unsigned char, 50> triangleBytes{};
    mesh.vertices.reserve(static_cast<size_t>(triangleCount) * 3);
    mesh.triangleIndices.reserve(static_cast<size_t>(triangleCount) * 3);
    for (uint32_t triangle = 0; triangle < triangleCount; ++triangle) {
        if (!file.read(reinterpret_cast<char*>(triangleBytes.data()), triangleBytes.size())) {
            return false;
        }

        int baseIndex = static_cast<int>(mesh.vertices.size());
        for (int vertex = 0; vertex < 3; ++vertex) {
            const unsigned char* p = triangleBytes.data() + 12 + vertex * 12;
            mesh.vertices.emplace_back(readF32Le(p), readF32Le(p + 4), readF32Le(p + 8));
            mesh.triangleIndices.push_back(baseIndex + vertex);
        }
    }

    return true;
}

bool readAsciiStl(const std::string& path, ImportedMeshData& mesh)
{
    std::ifstream file(path);
    if (!file) {
        return false;
    }

    std::string token;
    int facetVertexCount = 0;
    int baseIndex = 0;
    while (file >> token) {
        if (token != "vertex") {
            continue;
        }

        float x = 0.0f;
        float y = 0.0f;
        float z = 0.0f;
        if (!(file >> x >> y >> z)) {
            return false;
        }

        if (facetVertexCount == 0) {
            baseIndex = static_cast<int>(mesh.vertices.size());
        }

        mesh.vertices.emplace_back(x, y, z);
        ++facetVertexCount;
        if (facetVertexCount == 3) {
            mesh.triangleIndices.push_back(baseIndex);
            mesh.triangleIndices.push_back(baseIndex + 1);
            mesh.triangleIndices.push_back(baseIndex + 2);
            facetVertexCount = 0;
        }
    }

    return facetVertexCount == 0;
}

bool parseImportedStlFile(const std::string& path, const Vec3& scalePercent, ImportedMeshData& mesh)
{
    mesh = ImportedMeshData();
    if (path.empty()) {
        return false;
    }

    {
        std::lock_guard<std::mutex> cacheLock(gImportCacheMutex);
        if (gImportedStlCache.valid &&
            gImportedStlCache.path == path &&
            gImportedStlCache.scaleX == scalePercent.x &&
            gImportedStlCache.scaleY == scalePercent.y &&
            gImportedStlCache.scaleZ == scalePercent.z) {
            mesh = gImportedStlCache.mesh;
            return true;
        }
    }

    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        return false;
    }

    std::streamoff fileSize = file.tellg();
    if (fileSize < 84) {
        return false;
    }

    std::array<unsigned char, 4> countBytes{};
    file.seekg(80, std::ios::beg);
    if (!file.read(reinterpret_cast<char*>(countBytes.data()), countBytes.size())) {
        return false;
    }

    uint32_t binaryTriangleCount = readU32Le(countBytes.data());
    const std::streamoff expectedBinarySize =
        84 + static_cast<std::streamoff>(binaryTriangleCount) * 50;
    bool loaded = expectedBinarySize == fileSize
        ? readBinaryStl(path, binaryTriangleCount, mesh)
        : readAsciiStl(path, mesh);

    if (!loaded || mesh.vertices.empty() || mesh.triangleIndices.size() < 3) {
        return false;
    }

    centerAndScaleImportedMesh(mesh, scalePercent);

    {
        std::lock_guard<std::mutex> cacheLock(gImportCacheMutex);
        gImportedStlCache.path = path;
        gImportedStlCache.scaleX = scalePercent.x;
        gImportedStlCache.scaleY = scalePercent.y;
        gImportedStlCache.scaleZ = scalePercent.z;
        gImportedStlCache.mesh = mesh;
        gImportedStlCache.valid = true;
    }

    return true;
}

int sampleIdx(int x, int y, int z)
{
    return x + gState.sampleW * (y + gState.sampleH * z);
}

Vec3 samplePoint(int x, int y, int z)
{
    return Vec3(
        gState.gridMin.x + static_cast<float>(x - 1) * gState.voxelSize,
        gState.gridMin.y + static_cast<float>(y - 1) * gState.voxelSize,
        gState.gridMin.z + static_cast<float>(z - 1) * gState.voxelSize);
}

float meshGeometrySdf(const MeshCutterGeometry& mesh, const Vec3& p);

float boxSdf(const Vec3& p)
{
    Vec3 half = gState.localSize * 0.5f;
    float halfVoxel = gState.voxelSize * 0.5f;
    half.x = std::max(half.x - halfVoxel, halfVoxel);
    half.y = std::max(half.y - halfVoxel, halfVoxel);
    half.z = std::max(half.z - halfVoxel, halfVoxel);

    Vec3 q(std::fabs(p.x) - half.x, std::fabs(p.y) - half.y, std::fabs(p.z) - half.z);
    Vec3 outside(std::max(q.x, 0.0f), std::max(q.y, 0.0f), std::max(q.z, 0.0f));
    float inside = std::min(std::max(q.x, std::max(q.y, q.z)), 0.0f);
    return length(outside) + inside;
}

float cappedCylinderSdf(const Vec3& p, float radius)
{
    const float halfVoxel = gState.voxelSize * 0.5f;
    const float halfY = std::max(gState.localSize.y * 0.5f - halfVoxel, halfVoxel);
    const float safeRadius = std::max(radius, halfVoxel);
    const float radial = std::sqrt(p.x * p.x + p.z * p.z) - safeRadius;
    const float axial = std::fabs(p.y) - halfY;
    const float outsideRadial = std::max(radial, 0.0f);
    const float outsideAxial = std::max(axial, 0.0f);
    const float inside = std::min(std::max(radial, axial), 0.0f);
    return std::sqrt(outsideRadial * outsideRadial + outsideAxial * outsideAxial) + inside;
}

float tubeSdf(const Vec3& p)
{
    const float halfVoxel = gState.voxelSize * 0.5f;
    const float outerRadius = std::max(
        std::min(gState.localSize.x, gState.localSize.z) * 0.5f - halfVoxel,
        halfVoxel);
    const float innerRadius = clampF(
        gState.blankInnerRadius,
        0.0f,
        std::max(0.0f, outerRadius - halfVoxel));
    const float outer = cappedCylinderSdf(p, outerRadius);
    if (innerRadius <= halfVoxel * 0.25f) {
        return outer;
    }

    const float radial = std::sqrt(p.x * p.x + p.z * p.z);
    return std::max(outer, innerRadius - radial);
}

float stockSdf(const Vec3& p)
{
    switch (gState.blankShape) {
        case BlankShapeCylinder: {
            const float halfVoxel = gState.voxelSize * 0.5f;
            const float outerRadius = std::max(
                std::min(gState.localSize.x, gState.localSize.z) * 0.5f - halfVoxel,
                halfVoxel);
            return cappedCylinderSdf(p, outerRadius);
        }
        case BlankShapeTube:
            return tubeSdf(p);
        case BlankShapeHalfTube:
            return std::max(tubeSdf(p), -p.z);
        case BlankShapeImportedMesh:
            return gState.blankMesh
                ? meshGeometrySdf(*gState.blankMesh, p)
                : std::numeric_limits<float>::infinity();
        case BlankShapeBox:
        default:
            return boxSdf(p);
    }
}

float denseSdfSampleUnlocked(const Vec3& p)
{
    if (!gState.initialized ||
        gState.sampleW <= 0 ||
        gState.sampleH <= 0 ||
        gState.sampleD <= 0 ||
        gState.sdf.empty()) {
        return stockSdf(p);
    }

    const float gx = (p.x - gState.gridMin.x) / gState.voxelSize + 1.0f;
    const float gy = (p.y - gState.gridMin.y) / gState.voxelSize + 1.0f;
    const float gz = (p.z - gState.gridMin.z) / gState.voxelSize + 1.0f;

    const int x0 = clampI(static_cast<int>(std::floor(gx)), 0, gState.sampleW - 1);
    const int y0 = clampI(static_cast<int>(std::floor(gy)), 0, gState.sampleH - 1);
    const int z0 = clampI(static_cast<int>(std::floor(gz)), 0, gState.sampleD - 1);
    const int x1 = std::min(x0 + 1, gState.sampleW - 1);
    const int y1 = std::min(y0 + 1, gState.sampleH - 1);
    const int z1 = std::min(z0 + 1, gState.sampleD - 1);

    const float tx = clamp01(gx - static_cast<float>(x0));
    const float ty = clamp01(gy - static_cast<float>(y0));
    const float tz = clamp01(gz - static_cast<float>(z0));

    const float c000 = gState.sdf[static_cast<size_t>(sampleIdx(x0, y0, z0))];
    const float c100 = gState.sdf[static_cast<size_t>(sampleIdx(x1, y0, z0))];
    const float c010 = gState.sdf[static_cast<size_t>(sampleIdx(x0, y1, z0))];
    const float c110 = gState.sdf[static_cast<size_t>(sampleIdx(x1, y1, z0))];
    const float c001 = gState.sdf[static_cast<size_t>(sampleIdx(x0, y0, z1))];
    const float c101 = gState.sdf[static_cast<size_t>(sampleIdx(x1, y0, z1))];
    const float c011 = gState.sdf[static_cast<size_t>(sampleIdx(x0, y1, z1))];
    const float c111 = gState.sdf[static_cast<size_t>(sampleIdx(x1, y1, z1))];

    const float c00 = c000 + (c100 - c000) * tx;
    const float c10 = c010 + (c110 - c010) * tx;
    const float c01 = c001 + (c101 - c001) * tx;
    const float c11 = c011 + (c111 - c011) * tx;
    const float c0 = c00 + (c10 - c00) * ty;
    const float c1 = c01 + (c11 - c01) * ty;
    return c0 + (c1 - c0) * tz;
}

void initializeStockSdf()
{
    if (!gState.initialized) {
        return;
    }

    for (int z = 0; z < gState.sampleD; ++z) {
        for (int y = 0; y < gState.sampleH; ++y) {
            for (int x = 0; x < gState.sampleW; ++x) {
                Vec3 p = samplePoint(x, y, z);
                gState.sdf[static_cast<size_t>(sampleIdx(x, y, z))] = stockSdf(p);
            }
        }
    }
}

#if defined(SDF_USE_OPENVDB)
bool initializeImportedMeshSdfFromOpenVdb(
    const std::vector<openvdb::Vec3s>& points,
    const std::vector<openvdb::Vec3I>& triangles)
{
    if (!gState.initialized || points.empty() || triangles.empty()) {
        return false;
    }

    try {
        openvdb::initialize();
        const auto transform = openvdb::math::Transform::createLinearTransform(gState.voxelSize);
        openvdb::FloatGrid::Ptr meshGrid = openvdb::tools::meshToLevelSet<openvdb::FloatGrid>(
            *transform,
            points,
            triangles,
            3.0f);
        if (!meshGrid) {
            return false;
        }

        meshGrid->setName("imported_blank_sdf");
        openvdb::FloatGrid::ConstAccessor accessor = meshGrid->getConstAccessor();
        openvdb::tools::GridSampler<
            openvdb::FloatGrid::ConstAccessor,
            openvdb::tools::BoxSampler> sampler(accessor, meshGrid->transform());
        for (int z = 0; z < gState.sampleD; ++z) {
            for (int y = 0; y < gState.sampleH; ++y) {
                for (int x = 0; x < gState.sampleW; ++x) {
                    Vec3 p = samplePoint(x, y, z);
                    gState.sdf[static_cast<size_t>(sampleIdx(x, y, z))] =
                        static_cast<float>(sampler.wsSample(openvdb::Vec3d(p.x, p.y, p.z)));
                }
            }
        }

        gState.workpieceGrid = meshGrid;
        return true;
    } catch (const std::exception&) {
        return false;
    } catch (...) {
        return false;
    }
}

void initializeOpenVdbGrid()
{
    openvdb::initialize();

    const auto transform = openvdb::math::Transform::createLinearTransform(gState.voxelSize);
    const float halfVoxel = gState.voxelSize * 0.5f;
    const float halfX = std::max(gState.localSize.x * 0.5f - halfVoxel, halfVoxel);
    const float halfY = std::max(gState.localSize.y * 0.5f - halfVoxel, halfVoxel);
    const float halfZ = std::max(gState.localSize.z * 0.5f - halfVoxel, halfVoxel);
    const openvdb::Vec3s minPoint(
        -halfX,
        -halfY,
        -halfZ);
    const openvdb::Vec3s maxPoint(
        halfX,
        halfY,
        halfZ);

    const openvdb::math::BBox<openvdb::Vec3s> bounds(minPoint, maxPoint);
    gState.workpieceGrid = openvdb::tools::createLevelSetBox<openvdb::FloatGrid>(
        bounds,
        *transform,
        3.0f);

    if (gState.workpieceGrid) {
        gState.workpieceGrid->setName("workpiece_sdf");
    }
}

void updateOpenVdbWorkpieceAtPoint(
    openvdb::FloatGrid::Accessor& accessor,
    const openvdb::math::Transform& transform,
    const Vec3& point,
    float value)
{
    const openvdb::Coord coord = transform.worldToIndexCellCentered(
        openvdb::Vec3d(point.x, point.y, point.z));
    accessor.setValue(coord, value);
}
#endif

Vec3 closestPointOnSegment(const Vec3& a, const Vec3& b, const Vec3& p)
{
    Vec3 ab = b - a;
    float denom = lengthSqr(ab);
    if (denom <= 1.0e-12f) {
        return a;
    }

    float t = clamp01(dot(p - a, ab) / denom);
    return a + ab * t;
}

float profileRadiusForSamples(
    float axial,
    float radius,
    float height,
    const std::vector<float>& profileRadiusSamples,
    int profileSegmentCount)
{
    float h = std::max(height, gState.voxelSize * 1.0e-6f);
    float normalizedHeight = clamp01(axial / h);
    if (profileRadiusSamples.size() >= 2) {
        const float profilePosition = normalizedHeight * static_cast<float>(profileRadiusSamples.size() - 1);
        const size_t sampleIndex = std::min(
            static_cast<size_t>(std::floor(profilePosition)),
            profileRadiusSamples.size() - 2);
        const float t = clamp01(profilePosition - static_cast<float>(sampleIndex));
        const float a = clampF(profileRadiusSamples[sampleIndex], 0.0f, 2.0f);
        const float b = clampF(profileRadiusSamples[sampleIndex + 1], 0.0f, 2.0f);
        return radius * (a + (b - a) * t);
    }

    int segmentCount = std::max(2, profileSegmentCount);
    float profilePosition = normalizedHeight * static_cast<float>(segmentCount);
    int segment = std::min(static_cast<int>(std::floor(profilePosition)), segmentCount - 1);
    float t = clamp01(profilePosition - static_cast<float>(segment));
    float smoothT = t * t * (3.0f - 2.0f * t);
    float innerRadius = radius * 0.6535898f;
    float radiusA = (segment & 1) == 0 ? radius : innerRadius;
    float radiusB = ((segment + 1) & 1) == 0 ? radius : innerRadius;
    return radiusA + (radiusB - radiusA) * smoothT;
}

float profileRadius(float axial, float radius, float height)
{
    return profileRadiusForSamples(
        axial,
        radius,
        height,
        gState.profileRadiusSamples,
        gState.profileSegmentCount);
}

float profileDifferenceAtRoot(
    const Vec3& sample,
    const Vec3& root,
    const Vec3& axis,
    float radius,
    float height,
    const std::vector<float>& profileRadiusSamples,
    int profileSegmentCount)
{
    Vec3 toSample = sample - root;
    float axial = dot(toSample, axis);
    Vec3 radial = toSample - axis * axial;
    float radialDifference = profileRadiusForSamples(
        axial,
        radius,
        height,
        profileRadiusSamples,
        profileSegmentCount) - length(radial);
    float axialDifference = std::min(axial, height - axial);
    return std::min(radialDifference, axialDifference);
}

int sweptRootSampleCount(const Vec3& start, const Vec3& end, float geometryScale)
{
    (void)geometryScale;
    const float distance = length(end - start);
    const float step = std::max(gState.voxelSize * 0.5f, 0.000001f);
    return clampI(static_cast<int>(std::ceil(distance / step)), 1, 96);
}

float sweptProfileDifferenceForSamples(
    const Vec3& sample,
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    float radius,
    float height,
    const std::vector<float>& profileRadiusSamples,
    int profileSegmentCount)
{
    Vec3 motion = end - start;
    float geometryScale = std::max(radius, height);
    float epsilon = std::max(geometryScale, gState.voxelSize) * 1.0e-6f;

    if (lengthSqr(motion) <= epsilon * epsilon) {
        return profileDifferenceAtRoot(
            sample,
            start,
            axis,
            radius,
            height,
            profileRadiusSamples,
            profileSegmentCount);
    }

    const int segmentCount = sweptRootSampleCount(start, end, geometryScale);
    float best = -std::numeric_limits<float>::infinity();
    for (int i = 0; i <= segmentCount; ++i) {
        const float t = static_cast<float>(i) / static_cast<float>(segmentCount);
        best = std::max(
            best,
            profileDifferenceAtRoot(
                sample,
                start + motion * t,
                axis,
                radius,
                height,
                profileRadiusSamples,
                profileSegmentCount));
    }

    return best;
}

float sweptProfileDifference(
    const Vec3& sample,
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    float radius,
    float height)
{
    return sweptProfileDifferenceForSamples(
        sample,
        start,
        end,
        axis,
        radius,
        height,
        gState.profileRadiusSamples,
        gState.profileSegmentCount);
}

#if defined(SDF_USE_OPENVDB)
using CutterSampler = openvdb::tools::GridSampler<
    openvdb::FloatGrid::ConstAccessor,
    openvdb::tools::BoxSampler>;
#endif

bool cutterPointWithinExpandedBounds(const MeshCutterGeometry& cutter, const Vec3& p, float padding)
{
    return p.x >= cutter.boundsMin.x - padding &&
           p.y >= cutter.boundsMin.y - padding &&
           p.z >= cutter.boundsMin.z - padding &&
           p.x <= cutter.boundsMax.x + padding &&
           p.y <= cutter.boundsMax.y + padding &&
           p.z <= cutter.boundsMax.z + padding;
}

Vec3 closestPointOnTriangle(const Vec3& p, const Vec3& a, const Vec3& b, const Vec3& c)
{
    const Vec3 ab = b - a;
    const Vec3 ac = c - a;
    const Vec3 ap = p - a;
    const float d1 = dot(ab, ap);
    const float d2 = dot(ac, ap);
    if (d1 <= 0.0f && d2 <= 0.0f) {
        return a;
    }

    const Vec3 bp = p - b;
    const float d3 = dot(ab, bp);
    const float d4 = dot(ac, bp);
    if (d3 >= 0.0f && d4 <= d3) {
        return b;
    }

    const float vc = d1 * d4 - d3 * d2;
    if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f) {
        const float v = d1 / (d1 - d3);
        return a + ab * v;
    }

    const Vec3 cp = p - c;
    const float d5 = dot(ab, cp);
    const float d6 = dot(ac, cp);
    if (d6 >= 0.0f && d5 <= d6) {
        return c;
    }

    const float vb = d5 * d2 - d1 * d6;
    if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f) {
        const float w = d2 / (d2 - d6);
        return a + ac * w;
    }

    const float va = d3 * d6 - d5 * d4;
    if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f) {
        const float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
        return b + (c - b) * w;
    }

    const float denom = 1.0f / (va + vb + vc);
    const float v = vb * denom;
    const float w = vc * denom;
    return a + ab * v + ac * w;
}

float cutterTriangleDistanceSqr(const MeshCutterGeometry& cutter, const Vec3& p)
{
    if (cutter.vertices.empty() || cutter.triangleIndices.size() < 3) {
        return std::numeric_limits<float>::infinity();
    }

    float best = std::numeric_limits<float>::infinity();
    for (size_t i = 0; i + 2 < cutter.triangleIndices.size(); i += 3) {
        const int ia = cutter.triangleIndices[i];
        const int ib = cutter.triangleIndices[i + 1];
        const int ic = cutter.triangleIndices[i + 2];
        const Vec3& a = cutter.vertices[static_cast<size_t>(ia)];
        const Vec3& b = cutter.vertices[static_cast<size_t>(ib)];
        const Vec3& c = cutter.vertices[static_cast<size_t>(ic)];
        const Vec3 closest = closestPointOnTriangle(p, a, b, c);
        best = std::min(best, lengthSqr(p - closest));
    }

    return best;
}

bool rayIntersectsTriangle(const Vec3& origin, const Vec3& direction, const Vec3& a, const Vec3& b, const Vec3& c)
{
    const Vec3 edge1 = b - a;
    const Vec3 edge2 = c - a;
    const Vec3 h = cross(direction, edge2);
    const float det = dot(edge1, h);
    if (std::fabs(det) < 1.0e-7f) {
        return false;
    }

    const float invDet = 1.0f / det;
    const Vec3 s = origin - a;
    const float u = invDet * dot(s, h);
    const float barycentricTolerance = 1.0e-6f;
    if (u < -barycentricTolerance || u > 1.0f + barycentricTolerance) {
        return false;
    }

    const Vec3 q = cross(s, edge1);
    const float v = invDet * dot(direction, q);
    if (v < -barycentricTolerance || u + v > 1.0f + barycentricTolerance) {
        return false;
    }

    const float t = invDet * dot(edge2, q);
    return t > 1.0e-6f;
}

bool rayParityInside(const MeshCutterGeometry& cutter, const Vec3& origin, const Vec3& direction)
{
    int intersections = 0;
    for (size_t i = 0; i + 2 < cutter.triangleIndices.size(); i += 3) {
        const int ia = cutter.triangleIndices[i];
        const int ib = cutter.triangleIndices[i + 1];
        const int ic = cutter.triangleIndices[i + 2];
        if (rayIntersectsTriangle(
                origin,
                direction,
                cutter.vertices[static_cast<size_t>(ia)],
                cutter.vertices[static_cast<size_t>(ib)],
                cutter.vertices[static_cast<size_t>(ic)])) {
            ++intersections;
        }
    }

    return (intersections & 1) != 0;
}

bool cutterPointInsideByRayCast(const MeshCutterGeometry& cutter, const Vec3& p)
{
    if (!cutterPointWithinExpandedBounds(cutter, p, 1.0e-5f)) {
        return false;
    }

    const Vec3 directions[] = {
        normalize(Vec3(1.0f, 0.0f, 0.0f)),
        normalize(Vec3(0.731f, 0.281f, 0.622f)),
        normalize(Vec3(0.317f, 0.879f, 0.357f)),
        normalize(Vec3(0.541f, 0.617f, 0.572f)),
        normalize(Vec3(0.913f, 0.199f, 0.356f))
    };
    const Vec3 jitter(1.0e-5f, 2.0e-5f, 3.0e-5f);
    int insideVotes = 0;
    const int directionCount = static_cast<int>(sizeof(directions) / sizeof(directions[0]));
    for (int i = 0; i < directionCount; ++i) {
        if (rayParityInside(cutter, p + jitter * static_cast<float>(i + 1), directions[i])) {
            ++insideVotes;
        }
    }

    return insideVotes >= (directionCount / 2 + 1);
}

float meshGeometrySdf(const MeshCutterGeometry& mesh, const Vec3& p)
{
    if (mesh.vertices.empty() || mesh.triangleIndices.size() < 3) {
        return std::numeric_limits<float>::infinity();
    }

    const float distanceSqr = cutterTriangleDistanceSqr(mesh, p);
    if (!std::isfinite(distanceSqr)) {
        return std::numeric_limits<float>::infinity();
    }

    const float distance = std::sqrt(distanceSqr);
    return cutterPointInsideByRayCast(mesh, p) ? -distance : distance;
}

bool meshFitsStockEnvelope(const MeshCutterGeometry& mesh)
{
    const float maxAxisMm = 1000.0f;
    const float axisTolerance = 0.001f;
    const float envelopeTolerance = std::max(gState.voxelSize, 0.001f);
    const Vec3 size = mesh.boundsMax - mesh.boundsMin;
    if (size.x > maxAxisMm + axisTolerance ||
        size.y > maxAxisMm + axisTolerance ||
        size.z > maxAxisMm + axisTolerance) {
        return false;
    }

    const Vec3 half = gState.localSize * 0.5f;
    return mesh.boundsMin.x >= -half.x - envelopeTolerance &&
           mesh.boundsMin.y >= -half.y - envelopeTolerance &&
           mesh.boundsMin.z >= -half.z - envelopeTolerance &&
           mesh.boundsMax.x <= half.x + envelopeTolerance &&
           mesh.boundsMax.y <= half.y + envelopeTolerance &&
           mesh.boundsMax.z <= half.z + envelopeTolerance;
}

float triangleMeshCutterDifferenceAtLocalPoint(
    const MeshCutterGeometry& cutter,
    const Vec3& cutterPoint)
{
    const float boundsPadding = std::max(cutter.shellRadius, gState.voxelSize * 0.5f);
    if (!cutterPointWithinExpandedBounds(cutter, cutterPoint, boundsPadding)) {
        return -boundsPadding;
    }

    const float distanceSqr = cutterTriangleDistanceSqr(cutter, cutterPoint);
    if (!std::isfinite(distanceSqr)) {
        return -boundsPadding;
    }

    const float distance = std::sqrt(distanceSqr);
    return cutterPointInsideByRayCast(cutter, cutterPoint) ? distance : -distance;
}

Vec3 resolveRight(const Vec3& axis, const Vec3& requestedRight)
{
    Vec3 right = requestedRight - axis * dot(requestedRight, axis);
    if (lengthSqr(right) > 1.0e-12f) {
        return normalize(right);
    }

    Vec3 fallback = cross(axis, Vec3(0.0f, 0.0f, 1.0f));
    if (lengthSqr(fallback) <= 1.0e-12f) {
        fallback = cross(axis, Vec3(1.0f, 0.0f, 0.0f));
    }

    return normalize(fallback);
}

#if defined(SDF_USE_OPENVDB)
float meshCutterDifferenceAtRoot(
    const CutterSampler& sampler,
    const Vec3& sample,
    const Vec3& root,
    const Vec3& axis,
    const Vec3& right,
    float cutBand)
{
    Vec3 resolvedRight = resolveRight(axis, right);
    Vec3 forward = normalize(cross(resolvedRight, axis));
    Vec3 toSample = sample - root;
    openvdb::Vec3d cutterPoint(
        dot(toSample, resolvedRight),
        dot(toSample, axis),
        dot(toSample, forward));
    (void)cutBand;
    return -static_cast<float>(sampler.wsSample(cutterPoint));
}
#endif

float triangleMeshCutterDifferenceAtRoot(
    const MeshCutterGeometry& cutter,
    const Vec3& sample,
    const Vec3& root,
    const Vec3& axis,
    const Vec3& right)
{
    Vec3 resolvedRight = resolveRight(axis, right);
    Vec3 forward = normalize(cross(resolvedRight, axis));
    Vec3 toSample = sample - root;
    Vec3 cutterPoint(
        dot(toSample, resolvedRight),
        dot(toSample, axis),
        dot(toSample, forward));
    return triangleMeshCutterDifferenceAtLocalPoint(cutter, cutterPoint);
}

#if defined(SDF_USE_OPENVDB)
float sweptMeshCutterDifference(
    const CutterSampler& sampler,
    const Vec3& sample,
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    const Vec3& right,
    float cutBand)
{
    Vec3 motion = end - start;
    float epsilon = std::max(gState.voxelSize, 0.000001f) * 1.0e-6f;
    if (lengthSqr(motion) <= epsilon * epsilon) {
        return meshCutterDifferenceAtRoot(sampler, sample, start, axis, right, cutBand);
    }

    const int segmentCount = sweptRootSampleCount(
        start,
        end,
        std::max(length(end - start), gState.voxelSize));
    float best = -std::numeric_limits<float>::infinity();
    for (int i = 0; i <= segmentCount; ++i) {
        const float t = static_cast<float>(i) / static_cast<float>(segmentCount);
        best = std::max(
            best,
            meshCutterDifferenceAtRoot(
                sampler,
                sample,
                start + motion * t,
                axis,
                right,
                cutBand));
    }

    return best;
}
#endif

float sweptTriangleMeshCutterDifference(
    const MeshCutterGeometry& cutter,
    const Vec3& sample,
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    const Vec3& right)
{
    Vec3 motion = end - start;
    float epsilon = std::max(gState.voxelSize, 0.000001f) * 1.0e-6f;
    if (lengthSqr(motion) <= epsilon * epsilon) {
        return triangleMeshCutterDifferenceAtRoot(cutter, sample, start, axis, right);
    }

    const int segmentCount = sweptRootSampleCount(
        start,
        end,
        std::max(length(cutter.boundsMax - cutter.boundsMin), gState.voxelSize));
    float best = -std::numeric_limits<float>::infinity();
    for (int i = 0; i <= segmentCount; ++i) {
        const float t = static_cast<float>(i) / static_cast<float>(segmentCount);
        best = std::max(
            best,
            triangleMeshCutterDifferenceAtRoot(
                cutter,
                sample,
                start + motion * t,
                axis,
                right));
    }

    return best;
}

void expandBounds(Vec3& boundsMin, Vec3& boundsMax, const Vec3& p)
{
    boundsMin.x = std::min(boundsMin.x, p.x);
    boundsMin.y = std::min(boundsMin.y, p.y);
    boundsMin.z = std::min(boundsMin.z, p.z);
    boundsMax.x = std::max(boundsMax.x, p.x);
    boundsMax.y = std::max(boundsMax.y, p.y);
    boundsMax.z = std::max(boundsMax.z, p.z);
}

void cutterSweepBounds(
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    const Vec3& right,
    const Vec3& cutterMin,
    const Vec3& cutterMax,
    float padding,
    Vec3& boundsMin,
    Vec3& boundsMax)
{
    const Vec3 resolvedRight = resolveRight(axis, right);
    Vec3 forward = cross(resolvedRight, axis);
    if (lengthSqr(forward) <= 1.0e-12f) {
        forward = cross(axis, resolvedRight);
    }
    forward = normalize(forward);

    boundsMin = Vec3(
        std::numeric_limits<float>::infinity(),
        std::numeric_limits<float>::infinity(),
        std::numeric_limits<float>::infinity());
    boundsMax = Vec3(
        -std::numeric_limits<float>::infinity(),
        -std::numeric_limits<float>::infinity(),
        -std::numeric_limits<float>::infinity());

    for (int xi = 0; xi < 2; ++xi) {
        const float x = xi == 0 ? cutterMin.x : cutterMax.x;
        for (int yi = 0; yi < 2; ++yi) {
            const float y = yi == 0 ? cutterMin.y : cutterMax.y;
            for (int zi = 0; zi < 2; ++zi) {
                const float z = zi == 0 ? cutterMin.z : cutterMax.z;
                const Vec3 offset = resolvedRight * x + axis * y + forward * z;
                expandBounds(boundsMin, boundsMax, start + offset);
                expandBounds(boundsMin, boundsMax, end + offset);
            }
        }
    }

    boundsMin = boundsMin - Vec3(padding, padding, padding);
    boundsMax = boundsMax + Vec3(padding, padding, padding);
}

bool pointInsideBounds(const Vec3& p, const Vec3& boundsMin, const Vec3& boundsMax)
{
    return p.x >= boundsMin.x &&
           p.y >= boundsMin.y &&
           p.z >= boundsMin.z &&
           p.x <= boundsMax.x &&
           p.y <= boundsMax.y &&
           p.z <= boundsMax.z;
}

bool vectorsAligned(const Vec3& a, const Vec3& b, float minDot)
{
    if (lengthSqr(a) <= 1.0e-12f || lengthSqr(b) <= 1.0e-12f) {
        return true;
    }

    return dot(normalize(a), normalize(b)) >= minDot;
}

bool canMergeWithLastCutOperation(
    const CutOperation& previous,
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    const Vec3& right,
    float cutBand)
{
    if (previous.kind != CutOperationMesh) {
        return false;
    }

    if (previous.meshGeometry != gState.cutterMesh) {
        return false;
    }

#if defined(SDF_USE_OPENVDB)
    if (previous.cutterGrid != gState.cutterGrid) {
        return false;
    }
#endif

    const float tolerance = std::max(gState.voxelSize * 0.25f, 0.001f);
    if (std::fabs(previous.cutBand - cutBand) > tolerance) {
        return false;
    }

    const Vec3 previousAxis = normalize(previous.axis);
    const Vec3 nextAxis = normalize(axis);
    if (dot(previousAxis, nextAxis) < 0.9999f) {
        return false;
    }

    const Vec3 previousRight = resolveRight(previousAxis, previous.right);
    const Vec3 nextRight = resolveRight(nextAxis, right);
    if (dot(previousRight, nextRight) < 0.9999f) {
        return false;
    }

    const float continuityTolerance = std::max(gState.voxelSize * 2.0f, cutBand * 2.0f);
    if (lengthSqr(start - previous.end) > continuityTolerance * continuityTolerance) {
        return false;
    }

    return vectorsAligned(previous.end - previous.start, end - start, 0.999f);
}

bool profileSamplesEqual(const std::vector<float>& a, const std::vector<float>& b)
{
    if (a.size() != b.size()) {
        return false;
    }

    for (size_t i = 0; i < a.size(); ++i) {
        if (std::fabs(a[i] - b[i]) > 1.0e-6f) {
            return false;
        }
    }

    return true;
}

bool canMergeWithLastProfileCutOperation(
    const CutOperation& previous,
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    float radius,
    float height,
    float cutBand)
{
    if (previous.kind != CutOperationProfile) {
        return false;
    }

    const float tolerance = std::max(gState.voxelSize * 0.25f, 0.001f);
    if (std::fabs(previous.radius - radius) > tolerance ||
        std::fabs(previous.height - height) > tolerance ||
        std::fabs(previous.cutBand - cutBand) > tolerance ||
        previous.profileSegmentCount != gState.profileSegmentCount ||
        !profileSamplesEqual(previous.profileRadiusSamples, gState.profileRadiusSamples)) {
        return false;
    }

    const Vec3 previousAxis = normalize(previous.axis);
    const Vec3 nextAxis = normalize(axis);
    if (dot(previousAxis, nextAxis) < 0.9999f) {
        return false;
    }

    const float continuityTolerance = std::max(gState.voxelSize * 2.0f, cutBand * 2.0f);
    if (lengthSqr(start - previous.end) > continuityTolerance * continuityTolerance) {
        return false;
    }

    return vectorsAligned(previous.end - previous.start, end - start, 0.999f);
}

void recordMeshCutOperationUnlocked(
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    const Vec3& right,
    float cutBand)
{
    if (!gState.cutterMesh ||
        gState.cutterMesh->vertices.empty() ||
        gState.cutterMesh->triangleIndices.size() < 3) {
        return;
    }

    CutOperation operation;
    operation.kind = CutOperationMesh;
    operation.start = start;
    operation.end = end;
    operation.axis = axis;
    operation.right = right;
    operation.cutBand = cutBand;
    operation.meshGeometry = gState.cutterMesh;
#if defined(SDF_USE_OPENVDB)
    operation.cutterGrid = gState.cutterGrid;
#endif
    cutterSweepBounds(
        start,
        end,
        axis,
        right,
        operation.meshGeometry->boundsMin,
        operation.meshGeometry->boundsMax,
        std::max(cutBand, std::max(operation.meshGeometry->shellRadius, gState.voxelSize)),
        operation.boundsMin,
        operation.boundsMax);

    if (!gState.cutHistory.empty() &&
        canMergeWithLastCutOperation(
            gState.cutHistory.back(),
            start,
            end,
            axis,
            right,
            cutBand)) {
        CutOperation& previous = gState.cutHistory.back();
        previous.end = end;
        cutterSweepBounds(
            previous.start,
            previous.end,
            previous.axis,
            previous.right,
            previous.meshGeometry->boundsMin,
            previous.meshGeometry->boundsMax,
            std::max(previous.cutBand, std::max(previous.meshGeometry->shellRadius, gState.voxelSize)),
            previous.boundsMin,
            previous.boundsMax);
        return;
    }

    gState.cutHistory.push_back(operation);
}

void recordProfileCutOperationUnlocked(
    const Vec3& start,
    const Vec3& end,
    const Vec3& axis,
    float radius,
    float height,
    float cutBand)
{
    if (radius <= 0.0f || height <= 0.0f) {
        return;
    }

    CutOperation operation;
    operation.kind = CutOperationProfile;
    operation.start = start;
    operation.end = end;
    operation.axis = normalize(axis);
    operation.right = resolveRight(operation.axis, Vec3(1.0f, 0.0f, 0.0f));
    operation.cutBand = cutBand;
    operation.radius = radius;
    operation.height = height;
    operation.profileSegmentCount = gState.profileSegmentCount;
    operation.profileRadiusSamples = gState.profileRadiusSamples;
    cutterSweepBounds(
        start,
        end,
        operation.axis,
        operation.right,
        Vec3(-radius, 0.0f, -radius),
        Vec3(radius, height, radius),
        std::max(cutBand, gState.voxelSize),
        operation.boundsMin,
        operation.boundsMax);

    if (!gState.cutHistory.empty() &&
        canMergeWithLastProfileCutOperation(
            gState.cutHistory.back(),
            start,
            end,
            operation.axis,
            radius,
            height,
            cutBand)) {
        CutOperation& previous = gState.cutHistory.back();
        previous.end = end;
        cutterSweepBounds(
            previous.start,
            previous.end,
            previous.axis,
            previous.right,
            Vec3(-previous.radius, 0.0f, -previous.radius),
            Vec3(previous.radius, previous.height, previous.radius),
            std::max(previous.cutBand, gState.voxelSize),
            previous.boundsMin,
            previous.boundsMax);
        return;
    }

    gState.cutHistory.push_back(operation);
}

#if defined(SDF_USE_OPENVDB)
float sampleCutOperationWithSampler(
    const CutOperation& operation,
    const CutterSampler& sampler,
    const Vec3& p)
{
    if (!pointInsideBounds(p, operation.boundsMin, operation.boundsMax)) {
        return -std::numeric_limits<float>::infinity();
    }

    return sweptMeshCutterDifference(
        sampler,
        p,
        operation.start,
        operation.end,
        operation.axis,
        operation.right,
        operation.cutBand);
}
#endif

float sampleCutOperationUnlocked(const CutOperation& operation, const Vec3& p)
{
    if (!pointInsideBounds(p, operation.boundsMin, operation.boundsMax)) {
        return -std::numeric_limits<float>::infinity();
    }

    if (operation.kind == CutOperationProfile) {
        return sweptProfileDifferenceForSamples(
            p,
            operation.start,
            operation.end,
            operation.axis,
            operation.radius,
            operation.height,
            operation.profileRadiusSamples,
            operation.profileSegmentCount);
    }

#if defined(SDF_USE_OPENVDB)
    if (operation.cutterGrid) {
        openvdb::FloatGrid::ConstAccessor accessor = operation.cutterGrid->getConstAccessor();
        CutterSampler sampler(accessor, operation.cutterGrid->transform());
        return sampleCutOperationWithSampler(operation, sampler, p);
    }
#endif

    if (!operation.meshGeometry) {
        return -std::numeric_limits<float>::infinity();
    }

    return sweptTriangleMeshCutterDifference(
        *operation.meshGeometry,
        p,
        operation.start,
        operation.end,
        operation.axis,
        operation.right);
}

float sampleCutOperationExactUnlocked(const CutOperation& operation, const Vec3& p)
{
    if (!pointInsideBounds(p, operation.boundsMin, operation.boundsMax)) {
        return -std::numeric_limits<float>::infinity();
    }

    if (operation.kind == CutOperationProfile) {
        return sweptProfileDifferenceForSamples(
            p,
            operation.start,
            operation.end,
            operation.axis,
            operation.radius,
            operation.height,
            operation.profileRadiusSamples,
            operation.profileSegmentCount);
    }

    if (operation.meshGeometry) {
        return sweptTriangleMeshCutterDifference(
            *operation.meshGeometry,
            p,
            operation.start,
            operation.end,
            operation.axis,
            operation.right);
    }

#if defined(SDF_USE_OPENVDB)
    if (operation.cutterGrid) {
        openvdb::FloatGrid::ConstAccessor accessor = operation.cutterGrid->getConstAccessor();
        CutterSampler sampler(accessor, operation.cutterGrid->transform());
        return sampleCutOperationWithSampler(operation, sampler, p);
    }
#endif

    return -std::numeric_limits<float>::infinity();
}

float continuousSdfSampleUnlocked(const Vec3& p, bool exactMeshHistory)
{
    float value = gState.blankShape == BlankShapeImportedMesh
        ? denseSdfSampleUnlocked(p)
        : stockSdf(p);
    bool exactMeshOperationCoversPoint = false;
    for (const CutOperation& operation : gState.cutHistory) {
        if (exactMeshHistory &&
            operation.kind == CutOperationMesh &&
            operation.meshGeometry &&
            pointInsideBounds(p, operation.boundsMin, operation.boundsMax)) {
            exactMeshOperationCoversPoint = true;
        }

        value = std::max(
            value,
            exactMeshHistory
                ? sampleCutOperationExactUnlocked(operation, p)
                : sampleCutOperationUnlocked(operation, p));
    }

    if (!exactMeshHistory || !exactMeshOperationCoversPoint || gState.removalApplied) {
        value = std::max(value, denseSdfSampleUnlocked(p));
    }

    return value;
}

float continuousSdfSampleUnlocked(const Vec3& p)
{
    return continuousSdfSampleUnlocked(p, false);
}

float cutOnlySdfSampleUnlocked(const Vec3& p)
{
    float value = -std::numeric_limits<float>::infinity();
    for (const CutOperation& operation : gState.cutHistory) {
        value = std::max(value, sampleCutOperationUnlocked(operation, p));
    }

    return value;
}

float measurementPrecision(float requestedPrecision)
{
    if (!std::isfinite(requestedPrecision) || requestedPrecision <= 0.0f) {
        requestedPrecision = 0.001f;
    }

    return clampF(requestedPrecision, 0.000001f, 0.001f);
}

float measurementStep(float value, float precision, float maxStep)
{
    if (!std::isfinite(value)) {
        return maxStep;
    }

    const float distanceStep = std::fabs(value) * 0.75f;
    return clampF(distanceStep, precision, maxStep);
}

bool hasMeasurementSignChange(float a, float b, float precision)
{
    if (!std::isfinite(a) || !std::isfinite(b)) {
        return false;
    }

    if (std::fabs(a) <= precision || std::fabs(b) <= precision) {
        return true;
    }

    return (a < 0.0f && b > 0.0f) || (a > 0.0f && b < 0.0f);
}

bool refineMeasurementHit(
    const Vec3& origin,
    const Vec3& direction,
    float lowT,
    float lowValue,
    float highT,
    float highValue,
    float precision,
    bool exactMeshHistory,
    float& hitDistance,
    float& hitValue)
{
    if (std::fabs(lowValue) <= precision) {
        hitDistance = lowT;
        hitValue = lowValue;
        return true;
    }

    if (std::fabs(highValue) <= precision) {
        hitDistance = highT;
        hitValue = highValue;
        return true;
    }

    for (int i = 0; i < 80 && highT - lowT > precision; ++i) {
        const float midT = (lowT + highT) * 0.5f;
        const float midValue = continuousSdfSampleUnlocked(origin + direction * midT, exactMeshHistory);
        if (!std::isfinite(midValue)) {
            return false;
        }

        if (std::fabs(midValue) <= precision) {
            hitDistance = midT;
            hitValue = midValue;
            return true;
        }

        if ((lowValue < 0.0f && midValue < 0.0f) ||
            (lowValue > 0.0f && midValue > 0.0f)) {
            lowT = midT;
            lowValue = midValue;
        } else {
            highT = midT;
            highValue = midValue;
        }
    }

    hitDistance = (lowT + highT) * 0.5f;
    hitValue = continuousSdfSampleUnlocked(origin + direction * hitDistance, exactMeshHistory);
    return std::isfinite(hitValue);
}

bool findSurfaceAlongRayUnlocked(
    const Vec3& origin,
    const Vec3& direction,
    float startDistance,
    float maxDistance,
    float precision,
    float maxStep,
    int maxIterations,
    bool allowInitialHit,
    bool exactMeshHistory,
    float& hitDistance,
    float& hitValue)
{
    float lastT = clampF(startDistance, 0.0f, maxDistance);
    float lastValue = continuousSdfSampleUnlocked(origin + direction * lastT, exactMeshHistory);
    if (!std::isfinite(lastValue)) {
        return false;
    }

    if (allowInitialHit && std::fabs(lastValue) <= precision) {
        hitDistance = lastT;
        hitValue = lastValue;
        return true;
    }

    for (int i = 0; i < maxIterations && lastT < maxDistance; ++i) {
        float step = measurementStep(lastValue, precision, maxStep);
        if (!allowInitialHit && std::fabs(lastValue) <= precision) {
            step = std::max(step, precision);
        }

        const float nextT = std::min(lastT + step, maxDistance);
        if (nextT <= lastT + 1.0e-8f) {
            break;
        }

        const float nextValue = continuousSdfSampleUnlocked(origin + direction * nextT, exactMeshHistory);
        if (!std::isfinite(nextValue)) {
            return false;
        }

        if (hasMeasurementSignChange(lastValue, nextValue, precision)) {
            if (!allowInitialHit && std::fabs(lastValue) <= precision) {
                lastT = nextT;
                lastValue = nextValue;
                continue;
            }

            return refineMeasurementHit(
                origin,
                direction,
                lastT,
                lastValue,
                nextT,
                nextValue,
                precision,
                exactMeshHistory,
                hitDistance,
                hitValue);
        }

        lastT = nextT;
        lastValue = nextValue;
    }

    return false;
}

bool readyUnlocked()
{
    return gState.initialized &&
           gState.sampleW > 0 &&
           gState.sampleH > 0 &&
           gState.sampleD > 0 &&
           !gState.sdf.empty();
}

void bumpMaterialRevisionUnlocked()
{
    ++gState.materialRevision;
    if (gState.materialRevision == 0) {
        gState.materialRevision = 1;
    }
}

bool isSolidSampleValue(const std::vector<float>& sdf, int index)
{
    return index >= 0 &&
           index < static_cast<int>(sdf.size()) &&
           sdf[static_cast<size_t>(index)] <= 0.0f;
}

bool isCoreSolidSampleValue(const std::vector<float>& sdf, int index, float voxelSize)
{
    const float coreThreshold = std::max(voxelSize * 0.95f, 0.000001f);
    return isSolidSampleValue(sdf, index) &&
           sdf[static_cast<size_t>(index)] <= -coreThreshold;
}

bool isConnectivitySolidSampleValue(const std::vector<float>& sdf, int index, float voxelSize)
{
    const float connectionThreshold = std::max(voxelSize * 0.20f, 0.000001f);
    return isSolidSampleValue(sdf, index) &&
           sdf[static_cast<size_t>(index)] <= -connectionThreshold;
}

struct ConnectivityBuildResult {
    std::vector<int> removalIndices;
    int componentCount = 0;
    int keepCoreCount = 0;
};

ConnectivityBuildResult buildConnectivityRemovalIndices(
    const std::vector<float>& sdf,
    int sampleW,
    int sampleH,
    int sampleD,
    float voxelSize)
{
    ConnectivityBuildResult result;
    if (sdf.empty() || sampleW <= 0 || sampleH <= 0 || sampleD <= 0) {
        return result;
    }

    static constexpr int maxRemovalCandidatesPerPass = 750000;
    static constexpr int keepSurfaceExpansionSteps = 2;

    std::vector<unsigned char> solidState(sdf.size(), 0);
    std::vector<int> stack;
    std::vector<int> keepFrontier;
    std::vector<int> currentComponent;
    std::vector<int> keepComponent;
    stack.reserve(4096);
    keepFrontier.reserve(4096);
    currentComponent.reserve(4096);
    keepComponent.reserve(4096);
    result.removalIndices.reserve(4096);

    const int layerSize = sampleW * sampleH;
    static constexpr int neighborOffsets[6][3] = {
        { 1, 0, 0 },
        { -1, 0, 0 },
        { 0, 1, 0 },
        { 0, -1, 0 },
        { 0, 0, 1 },
        { 0, 0, -1 }
    };

    auto indexOf = [sampleW, sampleH](int x, int y, int z) {
        return x + sampleW * (y + sampleH * z);
    };

    auto expandKeepSurface = [&]() {
        std::vector<int> nextFrontier;
        nextFrontier.reserve(keepFrontier.size());
        for (int step = 0; step < keepSurfaceExpansionSteps && !keepFrontier.empty(); ++step) {
            nextFrontier.clear();
            for (int index : keepFrontier) {
                const int z = index / layerSize;
                const int layerOffset = index - z * layerSize;
                const int y = layerOffset / sampleW;
                const int x = layerOffset - y * sampleW;

                for (const auto& offset : neighborOffsets) {
                    const int nx = x + offset[0];
                    const int ny = y + offset[1];
                    const int nz = z + offset[2];
                    if (nx < 0 || nx >= sampleW ||
                        ny < 0 || ny >= sampleH ||
                        nz < 0 || nz >= sampleD) {
                        continue;
                    }

                    const int neighbor = indexOf(nx, ny, nz);
                    if (solidState[static_cast<size_t>(neighbor)] != 0 ||
                        !isSolidSampleValue(sdf, neighbor)) {
                        continue;
                    }

                    solidState[static_cast<size_t>(neighbor)] = 1;
                    nextFrontier.push_back(neighbor);
                }
            }
            keepFrontier.swap(nextFrontier);
        }
    };

    for (int start = 0; start < static_cast<int>(sdf.size()); ++start) {
        if (solidState[static_cast<size_t>(start)] != 0 ||
            !isConnectivitySolidSampleValue(sdf, start, voxelSize)) {
            continue;
        }

        ++result.componentCount;
        stack.clear();
        currentComponent.clear();
        solidState[static_cast<size_t>(start)] = 3;
        stack.push_back(start);

        size_t read = 0;
        while (read < stack.size()) {
            const int index = stack[read++];
            currentComponent.push_back(index);

            const int z = index / layerSize;
            const int layerOffset = index - z * layerSize;
            const int y = layerOffset / sampleW;
            const int x = layerOffset - y * sampleW;

            for (const auto& offset : neighborOffsets) {
                const int nx = x + offset[0];
                const int ny = y + offset[1];
                const int nz = z + offset[2];
                if (nx < 0 || nx >= sampleW ||
                    ny < 0 || ny >= sampleH ||
                    nz < 0 || nz >= sampleD) {
                    continue;
                }

                const int neighbor = indexOf(nx, ny, nz);
                if (solidState[static_cast<size_t>(neighbor)] != 0 ||
                    !isConnectivitySolidSampleValue(sdf, neighbor, voxelSize)) {
                    continue;
                }

                solidState[static_cast<size_t>(neighbor)] = 3;
                stack.push_back(neighbor);
            }
        }

        if (currentComponent.size() > keepComponent.size()) {
            keepComponent.swap(currentComponent);
        }
    }

    if (keepComponent.empty()) {
        return result;
    }

    result.keepCoreCount = static_cast<int>(keepComponent.size());
    keepFrontier.clear();
    for (int index : keepComponent) {
        solidState[static_cast<size_t>(index)] = 1;
        keepFrontier.push_back(index);
    }

    for (int index = 0; index < static_cast<int>(solidState.size()); ++index) {
        if (solidState[static_cast<size_t>(index)] == 3) {
            solidState[static_cast<size_t>(index)] = 0;
        }
    }
    expandKeepSurface();

    int removalComponentCount = 0;
    for (int start = 0; start < static_cast<int>(sdf.size()); ++start) {
        if (solidState[static_cast<size_t>(start)] != 0 ||
            !isSolidSampleValue(sdf, start)) {
            continue;
        }

        ++removalComponentCount;
        stack.clear();
        solidState[static_cast<size_t>(start)] = 2;
        stack.push_back(start);
        size_t read = 0;
        while (read < stack.size()) {
            const int index = stack[read++];
            if (static_cast<int>(result.removalIndices.size()) < maxRemovalCandidatesPerPass) {
                result.removalIndices.push_back(index);
            }

            const int z = index / layerSize;
            const int layerOffset = index - z * layerSize;
            const int y = layerOffset / sampleW;
            const int x = layerOffset - y * sampleW;

            for (const auto& offset : neighborOffsets) {
                const int nx = x + offset[0];
                const int ny = y + offset[1];
                const int nz = z + offset[2];
                if (nx < 0 || nx >= sampleW ||
                    ny < 0 || ny >= sampleH ||
                    nz < 0 || nz >= sampleD) {
                    continue;
                }

                const int neighbor = indexOf(nx, ny, nz);
                if (solidState[static_cast<size_t>(neighbor)] != 0 ||
                    !isSolidSampleValue(sdf, neighbor)) {
                    continue;
                }

                solidState[static_cast<size_t>(neighbor)] = 2;
                stack.push_back(neighbor);
            }
        }
    }

    result.componentCount = 1 + removalComponentCount;
    return result;
}

ConnectivityBuildResult buildLocalConnectivityRemovalIndices(
    const std::vector<float>& sdf,
    int localW,
    int localH,
    int localD,
    int originX,
    int originY,
    int originZ,
    int sampleW,
    int sampleH,
    int sampleD,
    float voxelSize)
{
    ConnectivityBuildResult result;
    if (sdf.empty() || localW <= 0 || localH <= 0 || localD <= 0 ||
        sampleW <= 0 || sampleH <= 0 || sampleD <= 0) {
        return result;
    }

    std::vector<int> labels(sdf.size(), -1);
    std::vector<int> stack;
    std::vector<int> componentSizes;
    std::vector<unsigned char> keepLabels;
    stack.reserve(4096);
    result.removalIndices.reserve(1024);

    const int localLayerSize = localW * localH;
    const int globalLayerSize = sampleW * sampleH;
    static constexpr int neighborOffsets[6][3] = {
        { 1, 0, 0 },
        { -1, 0, 0 },
        { 0, 1, 0 },
        { 0, -1, 0 },
        { 0, 0, 1 },
        { 0, 0, -1 }
    };

    for (int start = 0; start < static_cast<int>(sdf.size()); ++start) {
        if (labels[static_cast<size_t>(start)] >= 0 ||
            !isCoreSolidSampleValue(sdf, start, voxelSize)) {
            continue;
        }

        const int label = static_cast<int>(componentSizes.size());
        stack.clear();
        labels[static_cast<size_t>(start)] = label;
        stack.push_back(start);
        int componentSize = 0;

        while (!stack.empty()) {
            int index = stack.back();
            stack.pop_back();
            ++componentSize;

            const int z = index / localLayerSize;
            const int layerOffset = index - z * localLayerSize;
            const int y = layerOffset / localW;
            const int x = layerOffset - y * localW;

            for (const auto& offset : neighborOffsets) {
                const int nx = x + offset[0];
                const int ny = y + offset[1];
                const int nz = z + offset[2];
                if (nx < 0 || nx >= localW ||
                    ny < 0 || ny >= localH ||
                    nz < 0 || nz >= localD) {
                    continue;
                }

                const int neighbor = nx + localW * (ny + localH * nz);
                if (labels[static_cast<size_t>(neighbor)] >= 0 ||
                    !isCoreSolidSampleValue(sdf, neighbor, voxelSize)) {
                    continue;
                }

                labels[static_cast<size_t>(neighbor)] = label;
                stack.push_back(neighbor);
            }
        }

        componentSizes.push_back(componentSize);
    }

    if (componentSizes.empty()) {
        return result;
    }

    result.componentCount = static_cast<int>(componentSizes.size());
    keepLabels.assign(componentSizes.size(), 0);
    int largestLabel = 0;
    for (int label = 1; label < static_cast<int>(componentSizes.size()); ++label) {
        if (componentSizes[static_cast<size_t>(label)] > componentSizes[static_cast<size_t>(largestLabel)]) {
            largestLabel = label;
        }
    }
    result.keepCoreCount = componentSizes[static_cast<size_t>(largestLabel)];

    stack.clear();
    stack.reserve(sdf.size());
    for (int index = 0; index < static_cast<int>(sdf.size()); ++index) {
        if (labels[static_cast<size_t>(index)] >= 0) {
            stack.push_back(index);
        }
    }

    size_t read = 0;
    while (read < stack.size()) {
        const int index = stack[read++];
        const int label = labels[static_cast<size_t>(index)];
        const int z = index / localLayerSize;
        const int layerOffset = index - z * localLayerSize;
        const int y = layerOffset / localW;
        const int x = layerOffset - y * localW;

        const int gx = originX + x;
        const int gy = originY + y;
        const int gz = originZ + z;
        const bool touchesOpenRegionBoundary =
            (x == 0 && gx > 0) ||
            (x == localW - 1 && gx < sampleW - 1) ||
            (y == 0 && gy > 0) ||
            (y == localH - 1 && gy < sampleH - 1) ||
            (z == 0 && gz > 0) ||
            (z == localD - 1 && gz < sampleD - 1);
        if (touchesOpenRegionBoundary && label >= 0) {
            keepLabels[static_cast<size_t>(label)] = 1;
        }

        for (const auto& offset : neighborOffsets) {
            const int nx = x + offset[0];
            const int ny = y + offset[1];
            const int nz = z + offset[2];
            if (nx < 0 || nx >= localW ||
                ny < 0 || ny >= localH ||
                nz < 0 || nz >= localD) {
                continue;
            }

            const int neighbor = nx + localW * (ny + localH * nz);
            if (labels[static_cast<size_t>(neighbor)] >= 0 ||
                !isSolidSampleValue(sdf, neighbor)) {
                continue;
            }

            labels[static_cast<size_t>(neighbor)] = label;
            stack.push_back(neighbor);
        }
    }

    bool hasBoundaryKeep = false;
    for (unsigned char keep : keepLabels) {
        hasBoundaryKeep = hasBoundaryKeep || keep != 0;
    }
    if (!hasBoundaryKeep) {
        keepLabels[static_cast<size_t>(largestLabel)] = 1;
    }

    for (int index = 0; index < static_cast<int>(sdf.size()); ++index) {
        if (!isSolidSampleValue(sdf, index)) {
            continue;
        }

        const int label = labels[static_cast<size_t>(index)];
        if (label < 0 || keepLabels[static_cast<size_t>(label)] != 0) {
            continue;
        }

        const int z = index / localLayerSize;
        const int layerOffset = index - z * localLayerSize;
        const int y = layerOffset / localW;
        const int x = layerOffset - y * localW;
        const int globalIndex =
            (originX + x) + sampleW * ((originY + y) + sampleH * (originZ + z));
        if (globalIndex >= 0 && globalIndex < sampleW * sampleH * sampleD) {
            result.removalIndices.push_back(globalIndex);
        }
    }

    return result;
}

void joinConnectivityThread()
{
    if (gConnectivityThread.joinable()) {
        gConnectivityThread.join();
    }
}

void joinFinishedConnectivityThread()
{
    bool shouldJoin = false;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        shouldJoin = gConnectivityThread.joinable() && !gState.connectivityInProgress;
    }

    if (shouldJoin) {
        gConnectivityThread.join();
    }
}
int applyPendingRemovalUnlocked()
{
    if (!readyUnlocked() ||
        gState.pendingRemovalCount <= 0) {
        return 0;
    }

    const float airValue = std::max(gState.voxelSize * 2.0f, 0.000001f);
    int removed = 0;
#if defined(SDF_USE_OPENVDB)
    std::unique_ptr<openvdb::FloatGrid::Accessor> workpieceAccessor;
    if (gState.workpieceGrid) {
        workpieceAccessor = std::make_unique<openvdb::FloatGrid::Accessor>(gState.workpieceGrid->getAccessor());
    }
#endif

    for (int index : gState.pendingRemovalIndices) {
        if (index < 0 || index >= static_cast<int>(gState.sdf.size())) {
            continue;
        }

        float& value = gState.sdf[static_cast<size_t>(index)];
        if (value <= 0.0f) {
            value = airValue;
            ++removed;
#if defined(SDF_USE_OPENVDB)
            if (workpieceAccessor) {
                const int z = index / (gState.sampleW * gState.sampleH);
                const int layerOffset = index - z * gState.sampleW * gState.sampleH;
                const int y = layerOffset / gState.sampleW;
                const int x = layerOffset - y * gState.sampleW;
                const Vec3 p = samplePoint(x, y, z);
                updateOpenVdbWorkpieceAtPoint(*workpieceAccessor, gState.workpieceGrid->transform(), p, value);
            }
#endif
        }
    }

    gState.connectivityReady = false;
    gState.islandsFound = false;
    gState.pendingRemovalCount = 0;
    gState.pendingRemovalIndices.clear();
    if (removed > 0) {
        const int lastComponentCount = gState.lastConnectivityComponentCount;
        const int lastKeepCoreCount = gState.lastConnectivityKeepCoreCount;
        const int lastRemovalCandidateCount = gState.lastConnectivityRemovalCandidateCount;
        gState.removalApplied = true;
        bumpMaterialRevisionUnlocked();
        gState.lastConnectivityComponentCount = lastComponentCount;
        gState.lastConnectivityKeepCoreCount = lastKeepCoreCount;
        gState.lastConnectivityRemovalCandidateCount = lastRemovalCandidateCount;
    }
    return removed;
}

SampleBounds clampSampleBounds(
    int minX,
    int maxX,
    int minY,
    int maxY,
    int minZ,
    int maxZ)
{
    SampleBounds bounds;
    bounds.minX = clampI(minX, 0, gState.sampleW - 1);
    bounds.maxX = clampI(maxX, 0, gState.sampleW - 1);
    bounds.minY = clampI(minY, 0, gState.sampleH - 1);
    bounds.maxY = clampI(maxY, 0, gState.sampleH - 1);
    bounds.minZ = clampI(minZ, 0, gState.sampleD - 1);
    bounds.maxZ = clampI(maxZ, 0, gState.sampleD - 1);
    return bounds;
}

bool hasSamples(const SampleBounds& bounds)
{
    return bounds.minX <= bounds.maxX &&
           bounds.minY <= bounds.maxY &&
           bounds.minZ <= bounds.maxZ;
}

int cutCapsuleCpuUnlocked(const CapsuleCutRequest& request)
{
    if (!hasSamples(request.bounds)) {
        return 0;
    }

    const float affectedRadiusSqr = request.radius * request.radius;
    int changed = 0;

#if defined(SDF_USE_OPENVDB)
    std::unique_ptr<openvdb::FloatGrid::Accessor> workpieceAccessor;
    if (gState.workpieceGrid) {
        workpieceAccessor = std::make_unique<openvdb::FloatGrid::Accessor>(gState.workpieceGrid->getAccessor());
    }
#endif

    for (int z = request.bounds.minZ; z <= request.bounds.maxZ; ++z) {
        for (int y = request.bounds.minY; y <= request.bounds.maxY; ++y) {
            for (int x = request.bounds.minX; x <= request.bounds.maxX; ++x) {
                Vec3 p = samplePoint(x, y, z);
                Vec3 closest = closestPointOnSegment(request.start, request.end, p);
                Vec3 toSample = p - closest;
                if (lengthSqr(toSample) > affectedRadiusSqr) {
                    continue;
                }

                float cutterDifference = request.radius - length(toSample);
                float& oldValue = gState.sdf[static_cast<size_t>(sampleIdx(x, y, z))];
                if (cutterDifference > oldValue + gState.voxelSize * 0.0001f) {
                    oldValue = cutterDifference;
#if defined(SDF_USE_OPENVDB)
                    if (workpieceAccessor) {
                        updateOpenVdbWorkpieceAtPoint(*workpieceAccessor, gState.workpieceGrid->transform(), p, oldValue);
                    }
#endif
                    ++changed;
                }
            }
        }
    }

    return changed;
}

int cutProfileCpuUnlocked(const ProfileCutRequest& request)
{
    if (!hasSamples(request.bounds)) {
        return 0;
    }

    int changed = 0;

#if defined(SDF_USE_OPENVDB)
    std::unique_ptr<openvdb::FloatGrid::Accessor> workpieceAccessor;
    if (gState.workpieceGrid) {
        workpieceAccessor = std::make_unique<openvdb::FloatGrid::Accessor>(gState.workpieceGrid->getAccessor());
    }
#endif

    for (int z = request.bounds.minZ; z <= request.bounds.maxZ; ++z) {
        for (int y = request.bounds.minY; y <= request.bounds.maxY; ++y) {
            for (int x = request.bounds.minX; x <= request.bounds.maxX; ++x) {
                Vec3 p = samplePoint(x, y, z);
                float cutterDifference = sweptProfileDifference(
                    p,
                    request.start,
                    request.end,
                    request.axis,
                    request.radius,
                    request.height);
                if (cutterDifference < -request.cutBand) {
                    continue;
                }

                float& oldValue = gState.sdf[static_cast<size_t>(sampleIdx(x, y, z))];
                if (cutterDifference > oldValue + gState.voxelSize * 0.0001f) {
                    oldValue = cutterDifference;
#if defined(SDF_USE_OPENVDB)
                    if (workpieceAccessor) {
                        updateOpenVdbWorkpieceAtPoint(*workpieceAccessor, gState.workpieceGrid->transform(), p, oldValue);
                    }
#endif
                    ++changed;
                }
            }
        }
    }

    return changed;
}

int cutMeshCpuUnlocked(const MeshCutRequest& request)
{
    if (!request.cutter || !hasSamples(request.bounds)) {
        return 0;
    }

    int changed = 0;

#if defined(SDF_USE_OPENVDB)
    std::unique_ptr<CutterSampler> sampler;
    std::unique_ptr<openvdb::FloatGrid::ConstAccessor> cutterAccessor;
    if (gState.cutterGrid) {
        cutterAccessor = std::make_unique<openvdb::FloatGrid::ConstAccessor>(gState.cutterGrid->getConstAccessor());
        sampler = std::make_unique<CutterSampler>(*cutterAccessor, gState.cutterGrid->transform());
    }
    std::unique_ptr<openvdb::FloatGrid::Accessor> workpieceAccessor;
    if (gState.workpieceGrid) {
        workpieceAccessor = std::make_unique<openvdb::FloatGrid::Accessor>(gState.workpieceGrid->getAccessor());
    }
#endif

    for (int z = request.bounds.minZ; z <= request.bounds.maxZ; ++z) {
        for (int y = request.bounds.minY; y <= request.bounds.maxY; ++y) {
            for (int x = request.bounds.minX; x <= request.bounds.maxX; ++x) {
                Vec3 p = samplePoint(x, y, z);
#if defined(SDF_USE_OPENVDB)
                float cutterDifference = sampler
                    ? sweptMeshCutterDifference(
                        *sampler,
                        p,
                        request.start,
                        request.end,
                        request.axis,
                        request.right,
                        request.cutBand)
                    : sweptTriangleMeshCutterDifference(
                        *request.cutter,
                        p,
                        request.start,
                        request.end,
                        request.axis,
                        request.right);
#else
                float cutterDifference = sweptTriangleMeshCutterDifference(
                    *request.cutter,
                    p,
                    request.start,
                    request.end,
                    request.axis,
                    request.right);
#endif
                if (cutterDifference < -request.cutBand) {
                    continue;
                }

                float& oldValue = gState.sdf[static_cast<size_t>(sampleIdx(x, y, z))];
                if (cutterDifference > oldValue + gState.voxelSize * 0.0001f) {
                    oldValue = cutterDifference;
#if defined(SDF_USE_OPENVDB)
                    if (workpieceAccessor) {
                        updateOpenVdbWorkpieceAtPoint(*workpieceAccessor, gState.workpieceGrid->transform(), p, oldValue);
                    }
#endif
                    ++changed;
                }
            }
        }
    }

    return changed;
}

int dispatchCapsuleCutUnlocked(const CapsuleCutRequest& request)
{
    switch (gState.activeComputeBackend) {
        case ComputeBackendCpu:
        default:
            return cutCapsuleCpuUnlocked(request);
    }
}

int dispatchProfileCutUnlocked(const ProfileCutRequest& request)
{
    switch (gState.activeComputeBackend) {
        case ComputeBackendCpu:
        default:
            return cutProfileCpuUnlocked(request);
    }
}

int dispatchMeshCutUnlocked(const MeshCutRequest& request)
{
    switch (gState.activeComputeBackend) {
        case ComputeBackendCpu:
        default:
            return cutMeshCpuUnlocked(request);
    }
}

} // namespace

SDF_EXPORT void sdf_plugin_init(
    int w, int h, int d,
    int sampleW, int sampleH, int sampleD,
    float voxelSize,
    float gridMinX, float gridMinY, float gridMinZ,
    float localSizeX, float localSizeY, float localSizeZ,
    int blankShape,
    float blankInnerRadius)
{
    joinConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    gState = State();
    gState.width = std::max(1, w);
    gState.height = std::max(1, h);
    gState.depth = std::max(1, d);
    gState.sampleW = std::max(1, sampleW);
    gState.sampleH = std::max(1, sampleH);
    gState.sampleD = std::max(1, sampleD);
    gState.voxelSize = std::max(0.000001f, voxelSize);
    gState.gridMin = Vec3(gridMinX, gridMinY, gridMinZ);
    gState.localSize = Vec3(localSizeX, localSizeY, localSizeZ);
    gState.blankShape = clampI(blankShape, BlankShapeBox, BlankShapeImportedMesh);
    gState.blankInnerRadius = std::max(0.0f, blankInnerRadius);
    int64_t total = static_cast<int64_t>(gState.sampleW) * gState.sampleH * gState.sampleD;
    if (total <= 0 || total > static_cast<int64_t>(1 << 30)) {
        gState = State();
        return;
    }

    gState.sdf.assign(static_cast<size_t>(total), 0.0f);
    gState.initialized = true;
    initializeStockSdf();
    bumpMaterialRevisionUnlocked();
#if defined(SDF_USE_OPENVDB)
    initializeOpenVdbGrid();
#endif
}

SDF_EXPORT void sdf_plugin_shutdown()
{
    joinConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    gState = State();
}

SDF_EXPORT void sdf_plugin_reset()
{
    joinConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    initializeStockSdf();
    gState.removalApplied = false;
    gState.connectivityReady = false;
    gState.connectivityInProgress = false;
    gState.islandsFound = false;
    gState.pendingRemovalCount = 0;
    gState.pendingRemovalIndices.clear();
#if defined(SDF_USE_OPENVDB)
    gState.cutHistory.clear();
    initializeOpenVdbGrid();
#endif
    bumpMaterialRevisionUnlocked();
}

SDF_EXPORT int sdf_get_compute_backend_capabilities()
{
    return ComputeBackendCapabilityCpu;
}

SDF_EXPORT int sdf_set_preferred_compute_backend(int backend)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (backend != ComputeBackendCpu) {
        gState.preferredComputeBackend = ComputeBackendCpu;
        gState.activeComputeBackend = ComputeBackendCpu;
        return 0;
    }

    gState.preferredComputeBackend = ComputeBackendCpu;
    gState.activeComputeBackend = ComputeBackendCpu;
    return 1;
}

SDF_EXPORT int sdf_get_active_compute_backend()
{
    std::lock_guard<std::mutex> lock(gMutex);
    return gState.activeComputeBackend;
}

SDF_EXPORT int sdf_cut_capsule(
    float startX, float startY, float startZ,
    float endX, float endY, float endZ,
    float radius,
    int minX, int maxX,
    int minY, int maxY,
    int minZ, int maxZ)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || radius <= 0.0f) {
        return 0;
    }

    CapsuleCutRequest request;
    request.start = Vec3(startX, startY, startZ);
    request.end = Vec3(endX, endY, endZ);
    request.radius = radius;
    request.bounds = clampSampleBounds(minX, maxX, minY, maxY, minZ, maxZ);

    int changed = dispatchCapsuleCutUnlocked(request);
    if (changed > 0) {
        bumpMaterialRevisionUnlocked();
    }

    return changed;
}

SDF_EXPORT int sdf_cut_profile_cutter(
    float startX, float startY, float startZ,
    float endX, float endY, float endZ,
    float axisX, float axisY, float axisZ,
    float radius, float height, float updateBand,
    int minX, int maxX,
    int minY, int maxY,
    int minZ, int maxZ)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || radius <= 0.0f || height <= 0.0f) {
        return 0;
    }

    ProfileCutRequest request;
    request.start = Vec3(startX, startY, startZ);
    request.end = Vec3(endX, endY, endZ);
    request.axis = normalize(Vec3(axisX, axisY, axisZ));
    request.radius = radius;
    request.height = height;
    request.cutBand = std::max(0.0f, updateBand);
    request.bounds = clampSampleBounds(minX, maxX, minY, maxY, minZ, maxZ);

    int changed = dispatchProfileCutUnlocked(request);
    if (changed > 0) {
        recordProfileCutOperationUnlocked(
            request.start,
            request.end,
            request.axis,
            request.radius,
            request.height,
            request.cutBand);
        bumpMaterialRevisionUnlocked();
    }

    return changed;
}

SDF_EXPORT int sdf_cut_mesh_cutter(
    float startX, float startY, float startZ,
    float endX, float endY, float endZ,
    float axisX, float axisY, float axisZ,
    float rightX, float rightY, float rightZ,
    float updateBand,
    int minX, int maxX,
    int minY, int maxY,
    int minZ, int maxZ)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() ||
        !gState.cutterMesh ||
        gState.cutterMesh->vertices.empty() ||
        gState.cutterMesh->triangleIndices.size() < 3) {
        return 0;
    }

    MeshCutRequest request;
    request.cutter = gState.cutterMesh;
    request.start = Vec3(startX, startY, startZ);
    request.end = Vec3(endX, endY, endZ);
    request.axis = normalize(Vec3(axisX, axisY, axisZ));
    request.right = resolveRight(request.axis, Vec3(rightX, rightY, rightZ));
    request.cutBand = std::max(0.0f, updateBand);
    request.bounds = clampSampleBounds(minX, maxX, minY, maxY, minZ, maxZ);

    int changed = dispatchMeshCutUnlocked(request);
    if (changed > 0) {
        recordMeshCutOperationUnlocked(
            request.start,
            request.end,
            request.axis,
            request.right,
            request.cutBand);
        bumpMaterialRevisionUnlocked();
    }

    return changed;
}

SDF_EXPORT int sdf_cut_selected_cutter(
    float startX, float startY, float startZ,
    float endX, float endY, float endZ,
    float axisX, float axisY, float axisZ,
    float rightX, float rightY, float rightZ,
    float radius, float height, float updateBand,
    int minX, int maxX,
    int minY, int maxY,
    int minZ, int maxZ)
{
    bool hasCutterMesh = false;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        hasCutterMesh = gState.cutterMesh &&
            !gState.cutterMesh->vertices.empty() &&
            gState.cutterMesh->triangleIndices.size() >= 3;
    }

    if (hasCutterMesh) {
        return sdf_cut_mesh_cutter(
            startX, startY, startZ,
            endX, endY, endZ,
            axisX, axisY, axisZ,
            rightX, rightY, rightZ,
            updateBand,
            minX, maxX,
            minY, maxY,
            minZ, maxZ);
    }

    (void)startX;
    (void)startY;
    (void)startZ;
    (void)endX;
    (void)endY;
    (void)endZ;
    (void)axisX;
    (void)axisY;
    (void)axisZ;
    (void)rightX;
    (void)rightY;
    (void)rightZ;
    (void)radius;
    (void)height;
    (void)updateBand;
    (void)minX;
    (void)maxX;
    (void)minY;
    (void)maxY;
    (void)minZ;
    (void)maxZ;
    return 0;
}

SDF_EXPORT void sdf_check_connectivity()
{
    joinFinishedConnectivityThread();

    std::vector<float> snapshot;
    int sampleW = 0;
    int sampleH = 0;
    int sampleD = 0;
    float voxelSize = 0.0f;
    uint64_t revision = 0;

    try {
        std::lock_guard<std::mutex> lock(gMutex);
        if (gState.connectivityInProgress) {
            return;
        }

        gState.connectivityReady = false;
        gState.islandsFound = false;
        gState.pendingRemovalCount = 0;
        gState.lastConnectivityComponentCount = 0;
        gState.lastConnectivityKeepCoreCount = 0;
        gState.lastConnectivityRemovalCandidateCount = 0;
        gState.pendingRemovalIndices.clear();

        if (!readyUnlocked()) {
            gState.connectivityReady = true;
            return;
        }

        snapshot = gState.sdf;
        sampleW = gState.sampleW;
        sampleH = gState.sampleH;
        sampleD = gState.sampleD;
        voxelSize = gState.voxelSize;
        revision = gState.materialRevision;
        gState.connectivityInProgress = true;
    } catch (...) {
        std::lock_guard<std::mutex> lock(gMutex);
        gState.connectivityReady = true;
        gState.connectivityInProgress = false;
        gState.islandsFound = false;
        gState.pendingRemovalCount = 0;
        gState.lastConnectivityComponentCount = 0;
        gState.lastConnectivityKeepCoreCount = 0;
        gState.lastConnectivityRemovalCandidateCount = 0;
        gState.pendingRemovalIndices.clear();
        return;
    }

    try {
        gConnectivityThread = std::thread(
            [snapshot = std::move(snapshot), sampleW, sampleH, sampleD, voxelSize, revision]() mutable {
                ConnectivityBuildResult connectivityResult =
                    buildConnectivityRemovalIndices(snapshot, sampleW, sampleH, sampleD, voxelSize);
                snapshot.clear();
                snapshot.shrink_to_fit();

                std::lock_guard<std::mutex> lock(gMutex);
                if (gState.connectivityInProgress &&
                    readyUnlocked() &&
                    gState.sampleW == sampleW &&
                    gState.sampleH == sampleH &&
                    gState.sampleD == sampleD) {
                    (void)revision;
                    gState.pendingRemovalIndices = std::move(connectivityResult.removalIndices);
                    gState.pendingRemovalCount =
                        static_cast<int>(gState.pendingRemovalIndices.size());
                    gState.islandsFound = gState.pendingRemovalCount > 0;
                    gState.lastConnectivityComponentCount = connectivityResult.componentCount;
                    gState.lastConnectivityKeepCoreCount = connectivityResult.keepCoreCount;
                    gState.lastConnectivityRemovalCandidateCount = gState.pendingRemovalCount;
                } else {
                    gState.pendingRemovalIndices.clear();
                    gState.pendingRemovalCount = 0;
                    gState.islandsFound = false;
                    gState.lastConnectivityComponentCount = 0;
                    gState.lastConnectivityKeepCoreCount = 0;
                    gState.lastConnectivityRemovalCandidateCount = 0;
                }

                gState.connectivityReady = true;
                gState.connectivityInProgress = false;
            });
    } catch (...) {
        std::lock_guard<std::mutex> lock(gMutex);
        gState.connectivityReady = true;
        gState.connectivityInProgress = false;
        gState.islandsFound = false;
        gState.pendingRemovalCount = 0;
        gState.lastConnectivityComponentCount = 0;
        gState.lastConnectivityKeepCoreCount = 0;
        gState.lastConnectivityRemovalCandidateCount = 0;
        gState.pendingRemovalIndices.clear();
    }
}

SDF_EXPORT void sdf_check_connectivity_region(
    int minX,
    int maxX,
    int minY,
    int maxY,
    int minZ,
    int maxZ,
    int padding)
{
    (void)minX;
    (void)maxX;
    (void)minY;
    (void)maxY;
    (void)minZ;
    (void)maxZ;
    (void)padding;

    // A local window cannot prove that a component touching the window boundary
    // is still attached to the stock. Use the full native material state so
    // detached islands are removed by machining truth, not by viewport locality.
    sdf_check_connectivity();
}

SDF_EXPORT int sdf_is_connectivity_ready()
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    return gState.connectivityReady ? 1 : 0;
}

SDF_EXPORT int sdf_get_connectivity_result()
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    return gState.islandsFound ? 1 : 0;
}

SDF_EXPORT void sdf_consume_connectivity_result()
{
    std::lock_guard<std::mutex> lock(gMutex);
    gState.connectivityReady = false;
    gState.islandsFound = false;
    gState.pendingRemovalCount = 0;
    gState.lastConnectivityComponentCount = 0;
    gState.lastConnectivityKeepCoreCount = 0;
    gState.lastConnectivityRemovalCandidateCount = 0;
    gState.pendingRemovalIndices.clear();
}

SDF_EXPORT int sdf_apply_removal()
{
    std::lock_guard<std::mutex> lock(gMutex);
    return applyPendingRemovalUnlocked();
}

SDF_EXPORT int sdf_get_last_connectivity_component_count()
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    return gState.lastConnectivityComponentCount;
}

SDF_EXPORT int sdf_get_last_connectivity_keep_core_count()
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    return gState.lastConnectivityKeepCoreCount;
}

SDF_EXPORT int sdf_get_last_connectivity_removal_candidate_count()
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    return gState.lastConnectivityRemovalCandidateCount;
}

SDF_EXPORT int sdf_debug_connectivity_summary(
    int* solidSampleCount,
    int* coreSolidSampleCount,
    int* componentCount,
    int* keepCoreComponentCount,
    int* removalCandidateCount)
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    if (solidSampleCount != nullptr) {
        *solidSampleCount = 0;
    }
    if (coreSolidSampleCount != nullptr) {
        *coreSolidSampleCount = 0;
    }
    if (componentCount != nullptr) {
        *componentCount = 0;
    }
    if (keepCoreComponentCount != nullptr) {
        *keepCoreComponentCount = 0;
    }
    if (removalCandidateCount != nullptr) {
        *removalCandidateCount = 0;
    }

    if (!readyUnlocked()) {
        return 0;
    }

    int solid = 0;
    int coreSolid = 0;
    for (int index = 0; index < static_cast<int>(gState.sdf.size()); ++index) {
        if (isSolidSampleValue(gState.sdf, index)) {
            ++solid;
        }
        if (isCoreSolidSampleValue(gState.sdf, index, gState.voxelSize)) {
            ++coreSolid;
        }
    }

    ConnectivityBuildResult result = buildConnectivityRemovalIndices(
        gState.sdf,
        gState.sampleW,
        gState.sampleH,
        gState.sampleD,
        gState.voxelSize);

    if (solidSampleCount != nullptr) {
        *solidSampleCount = solid;
    }
    if (coreSolidSampleCount != nullptr) {
        *coreSolidSampleCount = coreSolid;
    }
    if (componentCount != nullptr) {
        *componentCount = result.componentCount;
    }
    if (keepCoreComponentCount != nullptr) {
        *keepCoreComponentCount = result.keepCoreCount;
    }
    if (removalCandidateCount != nullptr) {
        *removalCandidateCount = static_cast<int>(result.removalIndices.size());
    }

    return 1;
}

SDF_EXPORT int sdf_try_apply_connectivity_cleanup()
{
    joinFinishedConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked()) {
        return 0;
    }

    if (!gState.connectivityReady) {
        return -1;
    }

    if (!gState.islandsFound || gState.pendingRemovalCount <= 0) {
        gState.connectivityReady = false;
        gState.islandsFound = false;
        gState.pendingRemovalCount = 0;
        gState.pendingRemovalIndices.clear();
        return 0;
    }

    return applyPendingRemovalUnlocked();
}

SDF_EXPORT float sdf_sample_point(float x, float y, float z)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked()) {
        return std::numeric_limits<float>::infinity();
    }

    const Vec3 p(x, y, z);
    return continuousSdfSampleUnlocked(p);
}

SDF_EXPORT int sdf_sample_points(const float* points, float* values, int count)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || points == nullptr || values == nullptr || count <= 0) {
        return 0;
    }

    for (int i = 0; i < count; ++i) {
        const Vec3 p(
            points[i * 3 + 0],
            points[i * 3 + 1],
            points[i * 3 + 2]);
        values[i] = stockSdf(p);
    }

    for (const CutOperation& operation : gState.cutHistory) {
        if (operation.kind == CutOperationProfile) {
            for (int i = 0; i < count; ++i) {
                const Vec3 p(
                    points[i * 3 + 0],
                    points[i * 3 + 1],
                    points[i * 3 + 2]);
                values[i] = std::max(values[i], sampleCutOperationUnlocked(operation, p));
            }
            continue;
        }

#if defined(SDF_USE_OPENVDB)
        if (operation.kind == CutOperationMesh && operation.cutterGrid) {
            openvdb::FloatGrid::ConstAccessor accessor = operation.cutterGrid->getConstAccessor();
            CutterSampler sampler(accessor, operation.cutterGrid->transform());
            for (int i = 0; i < count; ++i) {
                const Vec3 p(
                    points[i * 3 + 0],
                    points[i * 3 + 1],
                    points[i * 3 + 2]);
                values[i] = std::max(values[i], sampleCutOperationWithSampler(operation, sampler, p));
            }
            continue;
        }
#endif

        if (operation.kind != CutOperationMesh || !operation.meshGeometry) {
            continue;
        }

        for (int i = 0; i < count; ++i) {
            const Vec3 p(
                points[i * 3 + 0],
                points[i * 3 + 1],
                points[i * 3 + 2]);
            if (!pointInsideBounds(p, operation.boundsMin, operation.boundsMax)) {
                continue;
            }

            values[i] = std::max(
                values[i],
                sweptTriangleMeshCutterDifference(
                    *operation.meshGeometry,
                    p,
                    operation.start,
                    operation.end,
                    operation.axis,
                    operation.right));
        }
    }

    for (int i = 0; i < count; ++i) {
        const Vec3 p(
            points[i * 3 + 0],
            points[i * 3 + 1],
            points[i * 3 + 2]);
        values[i] = std::max(values[i], denseSdfSampleUnlocked(p));
    }

    return count;
}

SDF_EXPORT float sdf_sample_cut_point(float x, float y, float z)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked()) {
        return -std::numeric_limits<float>::infinity();
    }

    return cutOnlySdfSampleUnlocked(Vec3(x, y, z));
}

SDF_EXPORT int sdf_sample_cut_points(const float* points, float* values, int count)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || points == nullptr || values == nullptr || count <= 0) {
        return 0;
    }

    for (int i = 0; i < count; ++i) {
        values[i] = -std::numeric_limits<float>::infinity();
    }

    for (const CutOperation& operation : gState.cutHistory) {
        if (operation.kind == CutOperationProfile) {
            for (int i = 0; i < count; ++i) {
                const Vec3 p(
                    points[i * 3 + 0],
                    points[i * 3 + 1],
                    points[i * 3 + 2]);
                values[i] = std::max(values[i], sampleCutOperationUnlocked(operation, p));
            }
            continue;
        }

#if defined(SDF_USE_OPENVDB)
        if (operation.kind == CutOperationMesh && operation.cutterGrid) {
            openvdb::FloatGrid::ConstAccessor accessor = operation.cutterGrid->getConstAccessor();
            CutterSampler sampler(accessor, operation.cutterGrid->transform());
            for (int i = 0; i < count; ++i) {
                const Vec3 p(
                    points[i * 3 + 0],
                    points[i * 3 + 1],
                    points[i * 3 + 2]);
                values[i] = std::max(values[i], sampleCutOperationWithSampler(operation, sampler, p));
            }
            continue;
        }
#endif

        if (operation.kind != CutOperationMesh || !operation.meshGeometry) {
            continue;
        }

        for (int i = 0; i < count; ++i) {
            const Vec3 p(
                points[i * 3 + 0],
                points[i * 3 + 1],
                points[i * 3 + 2]);
            if (!pointInsideBounds(p, operation.boundsMin, operation.boundsMax)) {
                continue;
            }

            values[i] = std::max(
                values[i],
                sweptTriangleMeshCutterDifference(
                    *operation.meshGeometry,
                    p,
                    operation.start,
                    operation.end,
                    operation.axis,
                    operation.right));
        }
    }

    return count;
}

SDF_EXPORT int sdf_measure_surface_ray(
    float originX,
    float originY,
    float originZ,
    float directionX,
    float directionY,
    float directionZ,
    float maxDistance,
    float precision,
    float maxStep,
    int maxIterations,
    float* hitDistance,
    float* hitX,
    float* hitY,
    float* hitZ,
    float* hitValue)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() ||
        hitDistance == nullptr ||
        hitX == nullptr ||
        hitY == nullptr ||
        hitZ == nullptr ||
        hitValue == nullptr ||
        !std::isfinite(maxDistance) ||
        maxDistance <= 0.0f) {
        return 0;
    }

    const Vec3 origin(originX, originY, originZ);
    const Vec3 rawDirection(directionX, directionY, directionZ);
    if (!std::isfinite(origin.x) ||
        !std::isfinite(origin.y) ||
        !std::isfinite(origin.z) ||
        !std::isfinite(rawDirection.x) ||
        !std::isfinite(rawDirection.y) ||
        !std::isfinite(rawDirection.z) ||
        lengthSqr(rawDirection) <= 1.0e-12f) {
        return 0;
    }

    const Vec3 direction = normalize(rawDirection);
    const float targetPrecision = measurementPrecision(precision);
    const float safeMaxStep = clampF(
        std::isfinite(maxStep) && maxStep > 0.0f ? maxStep : std::max(gState.voxelSize, targetPrecision),
        targetPrecision,
        maxDistance);
    const int safeMaxIterations = std::max(16, maxIterations);

    float distance = 0.0f;
    float value = 0.0f;
    if (!findSurfaceAlongRayUnlocked(
            origin,
            direction,
            0.0f,
            maxDistance,
            targetPrecision,
            safeMaxStep,
            safeMaxIterations,
            true,
            true,
            distance,
            value)) {
        return 0;
    }

    const Vec3 hit = origin + direction * distance;
    *hitDistance = distance;
    *hitX = hit.x;
    *hitY = hit.y;
    *hitZ = hit.z;
    *hitValue = value;
    return 1;
}

SDF_EXPORT int sdf_measure_thickness_ray(
    float originX,
    float originY,
    float originZ,
    float directionX,
    float directionY,
    float directionZ,
    float maxDistance,
    float precision,
    float maxStep,
    int maxIterations,
    float* entryDistance,
    float* exitDistance,
    float* thickness,
    float* entryX,
    float* entryY,
    float* entryZ,
    float* exitX,
    float* exitY,
    float* exitZ)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() ||
        entryDistance == nullptr ||
        exitDistance == nullptr ||
        thickness == nullptr ||
        entryX == nullptr ||
        entryY == nullptr ||
        entryZ == nullptr ||
        exitX == nullptr ||
        exitY == nullptr ||
        exitZ == nullptr ||
        !std::isfinite(maxDistance) ||
        maxDistance <= 0.0f) {
        return 0;
    }

    const Vec3 origin(originX, originY, originZ);
    const Vec3 rawDirection(directionX, directionY, directionZ);
    if (!std::isfinite(origin.x) ||
        !std::isfinite(origin.y) ||
        !std::isfinite(origin.z) ||
        !std::isfinite(rawDirection.x) ||
        !std::isfinite(rawDirection.y) ||
        !std::isfinite(rawDirection.z) ||
        lengthSqr(rawDirection) <= 1.0e-12f) {
        return 0;
    }

    const Vec3 direction = normalize(rawDirection);
    const float targetPrecision = measurementPrecision(precision);
    const float safeMaxStep = clampF(
        std::isfinite(maxStep) && maxStep > 0.0f ? maxStep : std::max(gState.voxelSize, targetPrecision),
        targetPrecision,
        maxDistance);
    const int safeMaxIterations = std::max(32, maxIterations);

    float firstDistance = 0.0f;
    float firstValue = 0.0f;
    if (!findSurfaceAlongRayUnlocked(
            origin,
            direction,
            0.0f,
            maxDistance,
            targetPrecision,
            safeMaxStep,
            safeMaxIterations,
            true,
            true,
            firstDistance,
            firstValue)) {
        return 0;
    }

    float secondDistance = 0.0f;
    float secondValue = 0.0f;
    const float secondStart = std::min(firstDistance + targetPrecision, maxDistance);
    if (secondStart >= maxDistance ||
        !findSurfaceAlongRayUnlocked(
            origin,
            direction,
            secondStart,
            maxDistance,
            targetPrecision,
            safeMaxStep,
            safeMaxIterations,
            false,
            true,
            secondDistance,
            secondValue) ||
        secondDistance <= firstDistance) {
        return 0;
    }

    const Vec3 entry = origin + direction * firstDistance;
    const Vec3 exit = origin + direction * secondDistance;
    *entryDistance = firstDistance;
    *exitDistance = secondDistance;
    *thickness = secondDistance - firstDistance;
    *entryX = entry.x;
    *entryY = entry.y;
    *entryZ = entry.z;
    *exitX = exit.x;
    *exitY = exit.y;
    *exitZ = exit.z;
    (void)firstValue;
    (void)secondValue;
    return 1;
}

SDF_EXPORT int sdf_get_cut_operation_count()
{
    std::lock_guard<std::mutex> lock(gMutex);
    const size_t count = gState.cutHistory.size();
    return count > static_cast<size_t>(std::numeric_limits<int>::max())
        ? std::numeric_limits<int>::max()
        : static_cast<int>(count);
}

SDF_EXPORT void sdf_get_data(float* buffer, int length)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || buffer == nullptr || length <= 0) {
        return;
    }

    int count = std::min(length, static_cast<int>(gState.sdf.size()));
    std::memcpy(buffer, gState.sdf.data(), static_cast<size_t>(count) * sizeof(float));
}

SDF_EXPORT int sdf_get_region(
    float* buffer,
    int length,
    int minX,
    int maxX,
    int minY,
    int maxY,
    int minZ,
    int maxZ)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || buffer == nullptr || length <= 0) {
        return 0;
    }

    minX = clampI(minX, 0, gState.sampleW - 1);
    maxX = clampI(maxX, 0, gState.sampleW - 1);
    minY = clampI(minY, 0, gState.sampleH - 1);
    maxY = clampI(maxY, 0, gState.sampleH - 1);
    minZ = clampI(minZ, 0, gState.sampleD - 1);
    maxZ = clampI(maxZ, 0, gState.sampleD - 1);
    if (maxX < minX || maxY < minY || maxZ < minZ) {
        return 0;
    }

    const int countX = maxX - minX + 1;
    const int countY = maxY - minY + 1;
    const int countZ = maxZ - minZ + 1;
    const int required = countX * countY * countZ;
    if (required <= 0 || length < required) {
        return 0;
    }

    int write = 0;
    for (int z = minZ; z <= maxZ; ++z) {
        for (int y = minY; y <= maxY; ++y) {
            const int src = sampleIdx(minX, y, z);
            std::memcpy(
                buffer + write,
                gState.sdf.data() + src,
                static_cast<size_t>(countX) * sizeof(float));
            write += countX;
        }
    }

    return write;
}

SDF_EXPORT int sdf_get_sample_count()
{
    std::lock_guard<std::mutex> lock(gMutex);
    return readyUnlocked() ? static_cast<int>(gState.sdf.size()) : 0;
}

SDF_EXPORT int sdf_is_ready()
{
    std::lock_guard<std::mutex> lock(gMutex);
    return readyUnlocked() ? 1 : 0;
}

SDF_EXPORT void sdf_set_profile_segment_count(int count)
{
    std::lock_guard<std::mutex> lock(gMutex);
    gState.profileSegmentCount = std::max(2, count);
}

SDF_EXPORT void sdf_set_profile_radius_samples(const float* samples, int count)
{
    std::lock_guard<std::mutex> lock(gMutex);
    gState.profileRadiusSamples.clear();
    if (samples == nullptr || count < 2) {
        return;
    }

    const int maxSamples = 64;
    const int safeCount = clampI(count, 2, maxSamples);
    gState.profileRadiusSamples.reserve(static_cast<size_t>(safeCount));
    bool hasRadius = false;
    for (int i = 0; i < safeCount; ++i) {
        float value = samples[i];
        if (!std::isfinite(value)) {
            value = 0.0f;
        }

        value = clampF(value, 0.0f, 2.0f);
        hasRadius = hasRadius || value > 1.0e-6f;
        gState.profileRadiusSamples.push_back(value);
    }

    if (!hasRadius) {
        gState.profileRadiusSamples.clear();
    }
}

SDF_EXPORT int sdf_openvdb_available()
{
#if defined(SDF_USE_OPENVDB)
    return 1;
#else
    return 0;
#endif
}

SDF_EXPORT int sdf_openvdb_active_voxel_count()
{
#if defined(SDF_USE_OPENVDB)
    std::lock_guard<std::mutex> lock(gMutex);
    if (!gState.workpieceGrid) {
        return 0;
    }

    const auto count = gState.workpieceGrid->activeVoxelCount();
    return count > static_cast<openvdb::Index64>(std::numeric_limits<int>::max())
        ? std::numeric_limits<int>::max()
        : static_cast<int>(count);
#else
    return 0;
#endif
}

int setImportedBlankMeshUnlocked(std::shared_ptr<MeshCutterGeometry> blankMesh)
{
    if (!readyUnlocked() || !blankMesh || blankMesh->vertices.empty() || blankMesh->triangleIndices.size() < 3) {
        return 0;
    }

#if defined(SDF_USE_OPENVDB)
    std::vector<openvdb::Vec3s> points;
    points.reserve(blankMesh->vertices.size());
    for (const Vec3& p : blankMesh->vertices) {
        points.emplace_back(p.x, p.y, p.z);
    }

    std::vector<openvdb::Vec3I> triangles;
    triangles.reserve(blankMesh->triangleIndices.size() / 3);
    for (size_t i = 0; i + 2 < blankMesh->triangleIndices.size(); i += 3) {
        triangles.emplace_back(
            blankMesh->triangleIndices[i],
            blankMesh->triangleIndices[i + 1],
            blankMesh->triangleIndices[i + 2]);
    }
#endif

    if (!meshFitsStockEnvelope(*blankMesh)) {
        return 0;
    }

    gState.blankMesh = blankMesh;
    gState.blankShape = BlankShapeImportedMesh;
    gState.cutHistory.clear();
    gState.removalApplied = false;
    gState.connectivityReady = false;
    gState.connectivityInProgress = false;
    gState.islandsFound = false;
    gState.pendingRemovalCount = 0;
    gState.pendingRemovalIndices.clear();
#if defined(SDF_USE_OPENVDB)
    if (!initializeImportedMeshSdfFromOpenVdb(points, triangles)) {
        gState.workpieceGrid.reset();
        initializeStockSdf();
    }
#else
    initializeStockSdf();
#endif
    bumpMaterialRevisionUnlocked();
    return 1;
}

std::shared_ptr<MeshCutterGeometry> createImportedBlankMeshFromArrays(
    const float* vertices,
    int vertexCount,
    const int* triangleIndices,
    int indexCount)
{
    if (vertices == nullptr ||
        triangleIndices == nullptr ||
        vertexCount <= 0 ||
        indexCount < 3) {
        return nullptr;
    }

    auto blankMesh = std::make_shared<MeshCutterGeometry>();
    blankMesh->vertices.reserve(static_cast<size_t>(vertexCount));
    Vec3 boundsMin(vertices[0], vertices[1], vertices[2]);
    Vec3 boundsMax = boundsMin;
    for (int i = 0; i < vertexCount; ++i) {
        Vec3 p(vertices[i * 3 + 0], vertices[i * 3 + 1], vertices[i * 3 + 2]);
        blankMesh->vertices.push_back(p);
        boundsMin.x = std::min(boundsMin.x, p.x);
        boundsMin.y = std::min(boundsMin.y, p.y);
        boundsMin.z = std::min(boundsMin.z, p.z);
        boundsMax.x = std::max(boundsMax.x, p.x);
        boundsMax.y = std::max(boundsMax.y, p.y);
        boundsMax.z = std::max(boundsMax.z, p.z);
    }

    std::vector<int> validTriangleIndices;
    validTriangleIndices.reserve(static_cast<size_t>(indexCount));
    for (int i = 0; i + 2 < indexCount; i += 3) {
        int a = triangleIndices[i];
        int b = triangleIndices[i + 1];
        int c = triangleIndices[i + 2];
        if (a < 0 || b < 0 || c < 0 ||
            a >= vertexCount || b >= vertexCount || c >= vertexCount ||
            a == b || b == c || c == a) {
            continue;
        }

        validTriangleIndices.push_back(a);
        validTriangleIndices.push_back(b);
        validTriangleIndices.push_back(c);
    }

    if (validTriangleIndices.empty()) {
        return nullptr;
    }

    blankMesh->triangleIndices = std::move(validTriangleIndices);
    blankMesh->boundsMin = boundsMin;
    blankMesh->boundsMax = boundsMax;
    blankMesh->shellRadius = 0.0f;
    return blankMesh;
}

std::shared_ptr<MeshCutterGeometry> createImportedBlankMeshFromParsedStl(const ImportedMeshData& mesh)
{
    if (mesh.vertices.empty() || mesh.triangleIndices.size() < 3) {
        return nullptr;
    }

    auto blankMesh = std::make_shared<MeshCutterGeometry>();
    blankMesh->vertices = mesh.vertices;
    blankMesh->triangleIndices = mesh.triangleIndices;
    blankMesh->boundsMin = mesh.boundsMin;
    blankMesh->boundsMax = mesh.boundsMax;
    blankMesh->shellRadius = 0.0f;
    return blankMesh;
}

SDF_EXPORT int sdf_get_stl_bounds(
    const char* filePath,
    float scalePercentX,
    float scalePercentY,
    float scalePercentZ,
    float* bounds6,
    int* triangleCount)
{
    if (bounds6 == nullptr || triangleCount == nullptr || filePath == nullptr) {
        return 0;
    }

    ImportedMeshData mesh;
    if (!parseImportedStlFile(
            std::string(filePath),
            sanitizeImportedScalePercent(scalePercentX, scalePercentY, scalePercentZ),
            mesh)) {
        return 0;
    }

    bounds6[0] = mesh.boundsMin.x;
    bounds6[1] = mesh.boundsMin.y;
    bounds6[2] = mesh.boundsMin.z;
    bounds6[3] = mesh.boundsMax.x;
    bounds6[4] = mesh.boundsMax.y;
    bounds6[5] = mesh.boundsMax.z;
    *triangleCount = static_cast<int>(mesh.triangleIndices.size() / 3);
    return 1;
}

SDF_EXPORT int sdf_set_blank_stl_file(
    const char* filePath,
    float scalePercentX,
    float scalePercentY,
    float scalePercentZ,
    float* bounds6,
    int* triangleCount)
{
    joinConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    if (!readyUnlocked() || filePath == nullptr) {
        return 0;
    }

    ImportedMeshData mesh;
    if (!parseImportedStlFile(
            std::string(filePath),
            sanitizeImportedScalePercent(scalePercentX, scalePercentY, scalePercentZ),
            mesh)) {
        return 0;
    }

    std::shared_ptr<MeshCutterGeometry> blankMesh = createImportedBlankMeshFromParsedStl(mesh);
    if (!setImportedBlankMeshUnlocked(blankMesh)) {
        return 0;
    }

    if (bounds6 != nullptr) {
        bounds6[0] = mesh.boundsMin.x;
        bounds6[1] = mesh.boundsMin.y;
        bounds6[2] = mesh.boundsMin.z;
        bounds6[3] = mesh.boundsMax.x;
        bounds6[4] = mesh.boundsMax.y;
        bounds6[5] = mesh.boundsMax.z;
    }

    if (triangleCount != nullptr) {
        *triangleCount = static_cast<int>(mesh.triangleIndices.size() / 3);
    }

    return 1;
}

SDF_EXPORT int sdf_set_blank_mesh(
    const float* vertices,
    int vertexCount,
    const int* triangleIndices,
    int indexCount)
{
    joinConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    std::shared_ptr<MeshCutterGeometry> blankMesh =
        createImportedBlankMeshFromArrays(vertices, vertexCount, triangleIndices, indexCount);
    return setImportedBlankMeshUnlocked(blankMesh);
}

SDF_EXPORT void sdf_clear_blank_mesh()
{
    joinConnectivityThread();
    std::lock_guard<std::mutex> lock(gMutex);
    gState.blankMesh.reset();
    if (gState.blankShape == BlankShapeImportedMesh) {
        gState.initialized = false;
        gState.sdf.clear();
        gState.cutHistory.clear();
        gState.removalApplied = false;
        gState.connectivityReady = false;
        gState.connectivityInProgress = false;
        gState.islandsFound = false;
        gState.pendingRemovalCount = 0;
        gState.pendingRemovalIndices.clear();
        bumpMaterialRevisionUnlocked();
#if defined(SDF_USE_OPENVDB)
        gState.workpieceGrid.reset();
#endif
    }
}

SDF_EXPORT int sdf_set_cutter_mesh(
    const float* vertices,
    int vertexCount,
    const int* triangleIndices,
    int indexCount,
    float voxelSize,
    float halfWidth)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (vertices == nullptr ||
        triangleIndices == nullptr ||
        vertexCount <= 0 ||
        indexCount < 3) {
        gState.cutterMesh.reset();
#if defined(SDF_USE_OPENVDB)
        gState.cutterGrid.reset();
#endif
        return 0;
    }

#if defined(SDF_USE_OPENVDB)
    std::vector<openvdb::Vec3s> points;
    points.reserve(static_cast<size_t>(vertexCount));
    for (int i = 0; i < vertexCount; ++i) {
        points.emplace_back(
            vertices[i * 3 + 0],
            vertices[i * 3 + 1],
            vertices[i * 3 + 2]);
    }
#endif

    auto cutterMesh = std::make_shared<MeshCutterGeometry>();
    cutterMesh->vertices.reserve(static_cast<size_t>(vertexCount));
    Vec3 boundsMin(vertices[0], vertices[1], vertices[2]);
    Vec3 boundsMax = boundsMin;
    for (int i = 0; i < vertexCount; ++i) {
        Vec3 p(vertices[i * 3 + 0], vertices[i * 3 + 1], vertices[i * 3 + 2]);
        cutterMesh->vertices.push_back(p);
        boundsMin.x = std::min(boundsMin.x, p.x);
        boundsMin.y = std::min(boundsMin.y, p.y);
        boundsMin.z = std::min(boundsMin.z, p.z);
        boundsMax.x = std::max(boundsMax.x, p.x);
        boundsMax.y = std::max(boundsMax.y, p.y);
        boundsMax.z = std::max(boundsMax.z, p.z);
    }

#if defined(SDF_USE_OPENVDB)
    std::vector<openvdb::Vec3I> triangles;
    triangles.reserve(static_cast<size_t>(indexCount / 3));
#endif
    std::vector<int> validTriangleIndices;
    validTriangleIndices.reserve(static_cast<size_t>(indexCount));
    for (int i = 0; i + 2 < indexCount; i += 3) {
        int a = triangleIndices[i];
        int b = triangleIndices[i + 1];
        int c = triangleIndices[i + 2];
        if (a < 0 || b < 0 || c < 0 ||
            a >= vertexCount || b >= vertexCount || c >= vertexCount ||
            a == b || b == c || c == a) {
            continue;
        }

#if defined(SDF_USE_OPENVDB)
        triangles.emplace_back(a, b, c);
#endif
        validTriangleIndices.push_back(a);
        validTriangleIndices.push_back(b);
        validTriangleIndices.push_back(c);
    }

    if (validTriangleIndices.empty()) {
        gState.cutterMesh.reset();
#if defined(SDF_USE_OPENVDB)
        gState.cutterGrid.reset();
#endif
        return 0;
    }

    const float safeVoxelSize = std::max(0.000001f, voxelSize);
    const float safeHalfWidth = std::max(1.0f, halfWidth);
    cutterMesh->triangleIndices = std::move(validTriangleIndices);
    cutterMesh->boundsMin = boundsMin;
    cutterMesh->boundsMax = boundsMax;
    cutterMesh->shellRadius = safeVoxelSize * safeHalfWidth;
    gState.cutterMesh = cutterMesh;

#if defined(SDF_USE_OPENVDB)
    try {
        openvdb::initialize();
        const auto transform = openvdb::math::Transform::createLinearTransform(safeVoxelSize);
        gState.cutterGrid = openvdb::tools::meshToLevelSet<openvdb::FloatGrid>(
            *transform,
            points,
            triangles,
            safeHalfWidth);

        if (!gState.cutterGrid) {
            return 1;
        }

        gState.cutterGrid->setName("cutter_sdf");
        return 1;
    } catch (const std::exception&) {
        gState.cutterGrid.reset();
        return 1;
    } catch (...) {
        gState.cutterGrid.reset();
        return 1;
    }
#else
    return 1;
#endif
}

SDF_EXPORT void sdf_clear_cutter_mesh()
{
    std::lock_guard<std::mutex> lock(gMutex);
    gState.cutterMesh.reset();
#if defined(SDF_USE_OPENVDB)
    gState.cutterGrid.reset();
#endif
}

SDF_EXPORT int sdf_get_cutter_active_voxel_count()
{
    std::lock_guard<std::mutex> lock(gMutex);
#if defined(SDF_USE_OPENVDB)
    if (!gState.cutterGrid) {
        return gState.cutterMesh ? 1 : 0;
    }

    const auto count = gState.cutterGrid->activeVoxelCount();
    return count > static_cast<openvdb::Index64>(std::numeric_limits<int>::max())
        ? std::numeric_limits<int>::max()
        : static_cast<int>(count);
#else
    return gState.cutterMesh ? 1 : 0;
#endif
}
