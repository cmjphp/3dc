using System.Globalization;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public sealed class WorkpieceSizeRuntimeUi : MonoBehaviour
{
    private const int WindowId = 731042;
    private readonly CultureInfo invariantCulture = CultureInfo.InvariantCulture;

    private CuttingDemoBootstrap bootstrap;
    private Rect windowRect = new Rect(16f, 16f, 300f, 680f);
    private string sizeX;
    private string sizeY;
    private string sizeZ;
    private string blankInnerRadius;
    private string importedBlankResourcePath;
    private string cutterDiameter;
    private string cutterHeight;
    private string moveSpeed;
    private string verticalSpeed;
    private string cutterDirectionX;
    private string cutterDirectionY;
    private string cutterDirectionZ;
    private string validationMessage;
    private int selectedBlankShapeIndex;
    private int selectedCutterToolIndex;
    private string[] cutterToolNames = new string[0];
    private readonly string[] blankShapeNames = { "盒", "圆柱", "管", "半管", "导入" };
    private bool collapsed;

    public void Configure(CuttingDemoBootstrap owner)
    {
        bootstrap = owner;
        SyncFromBootstrap();
    }

    private void OnEnable()
    {
        if (bootstrap == null)
        {
            bootstrap = GetComponent<CuttingDemoBootstrap>();
        }
        SyncFromBootstrap();
    }

    private void OnGUI()
    {
        if (bootstrap == null)
        {
            return;
        }

        windowRect.height = collapsed ? 54f : 820f;
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "加工参数");
    }

    private void DrawWindow(int windowId)
    {
        if (GUI.Button(new Rect(windowRect.width - 32f, 4f, 24f, 20f), collapsed ? "+" : "−"))
        {
            collapsed = !collapsed;
        }

        if (!collapsed)
        {
            float y = DrawWorkpieceDefinitionControls(30f);

            if (GUI.Button(new Rect(14f, y, 272f, 30f), "应用工件/毛坯"))
            {
                ApplyWorkpieceDefinition();
            }

            float cutterListLabelY = y + 44f;
            GUI.Label(new Rect(14f, cutterListLabelY, 270f, 20f), "刀具列表");
            DrawCutterToolList(cutterListLabelY + 28f);

            float cutterParamsY = cutterListLabelY + 70f;
            GUI.Label(new Rect(14f, cutterParamsY, 270f, 20f), "刀具参数 / mm、mm/s");
            DrawValueField("直径", ref cutterDiameter, cutterParamsY + 28f);
            DrawValueField("高度", ref cutterHeight, cutterParamsY + 58f);
            DrawValueField("水平速度", ref moveSpeed, cutterParamsY + 88f);
            DrawValueField("垂直速度", ref verticalSpeed, cutterParamsY + 118f);

            float applyCutterY = cutterParamsY + 151f;
            if (GUI.Button(new Rect(14f, applyCutterY, 272f, 30f), "应用刀具参数"))
            {
                ApplyCutterParameters();
            }

            float cutterDirectionYBase = applyCutterY + 43f;
            GUI.Label(new Rect(14f, cutterDirectionYBase, 270f, 20f), "刀具方向 / 度（切削轴）");
            DrawValueField("X 俯仰", ref cutterDirectionX, cutterDirectionYBase + 28f);
            DrawValueField("Y 偏航", ref cutterDirectionY, cutterDirectionYBase + 58f);
            DrawValueField("Z 滚转", ref cutterDirectionZ, cutterDirectionYBase + 88f);

            float applyDirectionY = cutterDirectionYBase + 121f;
            if (GUI.Button(new Rect(14f, applyDirectionY, 272f, 30f), "应用刀具方向"))
            {
                ApplyCutterDirection();
            }

            if (!string.IsNullOrEmpty(validationMessage))
            {
                GUI.Label(new Rect(14f, applyDirectionY + 40f, 272f, 32f), validationMessage);
            }
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 38f, 26f));
    }

    private void DrawValueField(string label, ref string value, float y)
    {
        GUI.Label(new Rect(14f, y, 86f, 24f), label);
        value = GUI.TextField(new Rect(104f, y, 182f, 24f), value ?? string.Empty);
    }

    private float DrawWorkpieceDefinitionControls(float y)
    {
        GUI.Label(new Rect(14f, y, 270f, 20f), "工件 / 毛坯参数 / mm");
        y += 28f;

        GUI.Label(new Rect(14f, y, 270f, 24f), "形状");
        int safeIndex = Mathf.Clamp(selectedBlankShapeIndex, 0, blankShapeNames.Length - 1);
        int nextIndex = GUI.Toolbar(new Rect(14f, y + 24f, 272f, 30f), safeIndex, blankShapeNames);
        if (nextIndex != safeIndex)
        {
            selectedBlankShapeIndex = nextIndex;
            SyncShapeFieldsFromCurrentDefinition(SelectedBlankShape);
        }

        y += 64f;

        switch (SelectedBlankShape)
        {
            case WorkpieceBlankShape.Box:
                DrawValueField("宽 X", ref sizeX, y);
                DrawValueField("高 Y", ref sizeY, y + 30f);
                DrawValueField("深 Z", ref sizeZ, y + 60f);
                return y + 96f;

            case WorkpieceBlankShape.Cylinder:
                DrawValueField("半径", ref sizeX, y);
                DrawValueField("高度", ref sizeY, y + 30f);
                return y + 66f;

            case WorkpieceBlankShape.Tube:
            case WorkpieceBlankShape.HalfTube:
                DrawValueField("外半径", ref sizeX, y);
                DrawValueField("内半径", ref blankInnerRadius, y + 30f);
                DrawValueField("高度", ref sizeY, y + 60f);
                return y + 96f;

            case WorkpieceBlankShape.ImportedMesh:
                DrawValueField("X 比例 %", ref sizeX, y);
                DrawValueField("Y 比例 %", ref sizeY, y + 30f);
                DrawValueField("Z 比例 %", ref sizeZ, y + 60f);
                DrawImportedModelSelector(y + 94f);
                return y + 146f;

            default:
                return y;
        }
    }

    private void DrawImportedModelSelector(float y)
    {
        GUI.Label(new Rect(14f, y, 86f, 24f), "导入模型");
        if (GUI.Button(new Rect(104f, y, 182f, 24f), "选择模型文件"))
        {
            SelectImportedBlankModel();
        }

        string displayPath = string.IsNullOrEmpty(importedBlankResourcePath)
            ? "未选择"
            : importedBlankResourcePath;
        GUI.Label(new Rect(104f, y + 28f, 182f, 20f), displayPath);
    }

    private void DrawCutterToolList(float y)
    {
        cutterToolNames = bootstrap.GetCutterToolNames();
        if (cutterToolNames == null || cutterToolNames.Length == 0)
        {
            GUI.Label(new Rect(14f, y, 272f, 24f), "暂无刀具");
            return;
        }

        int safeIndex = Mathf.Clamp(selectedCutterToolIndex, 0, cutterToolNames.Length - 1);
        int nextIndex = GUI.Toolbar(new Rect(14f, y, 272f, 30f), safeIndex, cutterToolNames);
        if (nextIndex == safeIndex)
        {
            return;
        }

        bootstrap.SelectCutterTool(nextIndex);
        SyncFromBootstrap();
        validationMessage = $"已选择：{bootstrap.SelectedCutterToolName}";
    }

    private void ApplyWorkpieceDefinition()
    {
        WorkpieceBlankShape shape = SelectedBlankShape;
        if (shape == WorkpieceBlankShape.ImportedMesh &&
            string.IsNullOrWhiteSpace(importedBlankResourcePath))
        {
            validationMessage = "请先选择导入模型文件。";
            return;
        }

        if (!TryBuildWorkpieceDefinition(shape, out Vector3 requested, out float innerRadius))
        {
            return;
        }

        bool applied = bootstrap.SetWorkpieceDefinition(requested, shape, innerRadius, importedBlankResourcePath);
        SyncFromBootstrap();
        if (!applied)
        {
            validationMessage = shape == WorkpieceBlankShape.ImportedMesh
                ? "导入工件失败：请确认模型在 Resources 下且尺寸未超限。"
                : "应用工件失败：原生 SDF 未就绪。";
            return;
        }

        validationMessage =
            shape == WorkpieceBlankShape.ImportedMesh
                ? $"工件：导入 {importedBlankResourcePath}"
                : $"工件：{blankShapeNames[(int)shape]}";
    }

    private bool TryBuildWorkpieceDefinition(
        WorkpieceBlankShape shape,
        out Vector3 size,
        out float innerRadius)
    {
        size = Vector3.zero;
        innerRadius = 0f;

        switch (shape)
        {
            case WorkpieceBlankShape.Box:
                return TryParseSize3("请输入有效的宽、高、深。", out size);

            case WorkpieceBlankShape.Cylinder:
                if (!TryParseRadiusAndHeight(out float cylinderRadius, out float cylinderHeight))
                {
                    return false;
                }

                size = new Vector3(cylinderRadius * 2f, cylinderHeight, cylinderRadius * 2f);
                return true;

            case WorkpieceBlankShape.Tube:
            case WorkpieceBlankShape.HalfTube:
                if (!TryParseRadiusAndHeight(out float outerRadius, out float tubeHeight) ||
                    !TryParseBounded(
                        blankInnerRadius,
                        0f,
                        CuttingDemoBootstrap.MaxWorkpieceSizeMm * 0.5f,
                        out innerRadius))
                {
                    validationMessage = "请输入有效的外半径、内半径和高度。";
                    return false;
                }

                if (innerRadius >= outerRadius)
                {
                    validationMessage = "内半径必须小于外半径。";
                    return false;
                }

                size = new Vector3(outerRadius * 2f, tubeHeight, outerRadius * 2f);
                return true;

            case WorkpieceBlankShape.ImportedMesh:
                return TryParseImportedScalePercent(out size);

            default:
                validationMessage = "未知的毛坯形状。";
                return false;
        }
    }

    private bool TryParseSize3(string errorMessage, out Vector3 size)
    {
        size = Vector3.zero;
        if (!TryParseBounded(
                sizeX,
                CuttingDemoBootstrap.MinWorkpieceSizeMm,
                CuttingDemoBootstrap.MaxWorkpieceSizeMm,
                out float x) ||
            !TryParseBounded(
                sizeY,
                CuttingDemoBootstrap.MinWorkpieceSizeMm,
                CuttingDemoBootstrap.MaxWorkpieceSizeMm,
                out float y) ||
            !TryParseBounded(
                sizeZ,
                CuttingDemoBootstrap.MinWorkpieceSizeMm,
                CuttingDemoBootstrap.MaxWorkpieceSizeMm,
                out float z))
        {
            validationMessage = errorMessage;
            return false;
        }

        size = new Vector3(x, y, z);
        return true;
    }

    private bool TryParseRadiusAndHeight(out float radius, out float height)
    {
        bool parsedRadius = TryParseBounded(
            sizeX,
            CuttingDemoBootstrap.MinWorkpieceSizeMm * 0.5f,
            CuttingDemoBootstrap.MaxWorkpieceSizeMm * 0.5f,
            out radius);
        bool parsedHeight = TryParseBounded(
            sizeY,
            CuttingDemoBootstrap.MinWorkpieceSizeMm,
            CuttingDemoBootstrap.MaxWorkpieceSizeMm,
            out height);
        bool parsed = parsedRadius && parsedHeight;
        if (!parsed)
        {
            validationMessage = "请输入有效的半径和高度。";
        }

        return parsed;
    }

    private bool TryParseImportedScalePercent(out Vector3 scalePercent)
    {
        scalePercent = Vector3.one * 100f;
        if (!TryParseBounded(sizeX, 0.001f, 1000f, out float x) ||
            !TryParseBounded(sizeY, 0.001f, 1000f, out float y) ||
            !TryParseBounded(sizeZ, 0.001f, 1000f, out float z))
        {
            validationMessage = "请输入有效的 X/Y/Z 缩放百分比。";
            return false;
        }

        scalePercent = new Vector3(x, y, z);
        return true;
    }

    private WorkpieceBlankShape SelectedBlankShape
    {
        get
        {
            return (WorkpieceBlankShape)Mathf.Clamp(
                selectedBlankShapeIndex,
                0,
                blankShapeNames.Length - 1);
        }
    }

    private void SelectImportedBlankModel()
    {
#if UNITY_EDITOR
        string absolutePath = EditorUtility.OpenFilePanel(
            "选择导入毛坯模型",
            Application.dataPath,
            "fbx,obj");
        if (string.IsNullOrEmpty(absolutePath))
        {
            return;
        }

        string projectPath = Application.dataPath.Replace('\\', '/');
        string normalizedPath = absolutePath.Replace('\\', '/');
        if (!normalizedPath.StartsWith(projectPath + "/", System.StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = "请选择项目 Assets/Resources 下的模型。";
            return;
        }

        string assetPath = "Assets" + normalizedPath.Substring(projectPath.Length);
        const string resourcesPrefix = "Assets/Resources/";
        if (!assetPath.StartsWith(resourcesPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = "模型需位于 Assets/Resources 下。";
            return;
        }

        importedBlankResourcePath = assetPath.Substring(resourcesPrefix.Length);
        int extensionIndex = importedBlankResourcePath.LastIndexOf('.');
        if (extensionIndex > 0)
        {
            importedBlankResourcePath = importedBlankResourcePath.Substring(0, extensionIndex);
        }

        validationMessage = $"已选择：{importedBlankResourcePath}";
#else
        validationMessage = "当前运行环境不支持文件选择。";
#endif
    }

    private void ApplyCutterParameters()
    {
        if (!TryParseBounded(
                cutterDiameter,
                CuttingDemoBootstrap.MinCutterSizeMm,
                CuttingDemoBootstrap.MaxCutterSizeMm,
                out float diameter) ||
            !TryParseBounded(
                cutterHeight,
                CuttingDemoBootstrap.MinCutterSizeMm,
                CuttingDemoBootstrap.MaxCutterSizeMm,
                out float height) ||
            !TryParseBounded(
                moveSpeed,
                CuttingDemoBootstrap.MinCutterSpeedMmPerSecond,
                CuttingDemoBootstrap.MaxCutterSpeedMmPerSecond,
                out float horizontal) ||
            !TryParseBounded(
                verticalSpeed,
                CuttingDemoBootstrap.MinCutterSpeedMmPerSecond,
                CuttingDemoBootstrap.MaxCutterSpeedMmPerSecond,
                out float vertical))
        {
            validationMessage = "请输入有效的刀具参数。";
            return;
        }

        bootstrap.SetCutterParameters(diameter, height, horizontal, vertical);
        SyncFromBootstrap();
        validationMessage =
            $"刀具：直径 {cutterDiameter}，高度 {cutterHeight} mm";
    }

    private void ApplyCutterDirection()
    {
        if (!TryParseBounded(
                cutterDirectionX,
                CuttingDemoBootstrap.MinCutterDirectionDegrees,
                CuttingDemoBootstrap.MaxCutterDirectionDegrees,
                out float x) ||
            !TryParseBounded(
                cutterDirectionY,
                CuttingDemoBootstrap.MinCutterDirectionDegrees,
                CuttingDemoBootstrap.MaxCutterDirectionDegrees,
                out float y) ||
            !TryParseBounded(
                cutterDirectionZ,
                CuttingDemoBootstrap.MinCutterDirectionDegrees,
                CuttingDemoBootstrap.MaxCutterDirectionDegrees,
                out float z))
        {
            validationMessage = "请输入有效的刀具方向。";
            return;
        }

        bootstrap.SetCutterDirectionEuler(new Vector3(x, y, z));
        SyncFromBootstrap();
        validationMessage =
            $"方向：X {cutterDirectionX}°，Y {cutterDirectionY}°，Z {cutterDirectionZ}°";
    }

    private static bool TryParseBounded(string text, float min, float max, out float value)
    {
        bool parsed = float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
        if (!parsed)
        {
            parsed = float.TryParse(text, out value);
        }

        if (!parsed || float.IsNaN(value) || float.IsInfinity(value))
        {
            return false;
        }

        value = Mathf.Clamp(value, min, max);
        return true;
    }

    private void SyncFromBootstrap()
    {
        if (bootstrap == null)
        {
            return;
        }

        selectedBlankShapeIndex = Mathf.Clamp((int)bootstrap.blankShape, 0, blankShapeNames.Length - 1);
        SyncShapeFieldsFromCurrentDefinition(SelectedBlankShape);
        importedBlankResourcePath = bootstrap.importedBlankModelResourcePath ?? string.Empty;
        cutterDiameter = (bootstrap.cutterRadius * 2f).ToString("0.###", invariantCulture);
        cutterHeight = (bootstrap.cutterRadius * bootstrap.cutterHeightScale).ToString(
            "0.###",
            invariantCulture);
        moveSpeed = bootstrap.cutterMoveSpeedMmPerSecond.ToString("0.###", invariantCulture);
        verticalSpeed = bootstrap.cutterVerticalSpeedMmPerSecond.ToString("0.###", invariantCulture);
        Vector3 direction = bootstrap.cutterDirectionEuler;
        cutterDirectionX = direction.x.ToString("0.###", invariantCulture);
        cutterDirectionY = direction.y.ToString("0.###", invariantCulture);
        cutterDirectionZ = direction.z.ToString("0.###", invariantCulture);
        selectedCutterToolIndex = bootstrap.selectedCutterToolIndex;
        cutterToolNames = bootstrap.GetCutterToolNames();
    }

    private void SyncShapeFieldsFromCurrentDefinition(WorkpieceBlankShape shape)
    {
        if (bootstrap == null)
        {
            return;
        }

        Vector3 size = bootstrap.workpieceSizeMm;
        switch (shape)
        {
            case WorkpieceBlankShape.Cylinder:
                sizeX = (Mathf.Min(size.x, size.z) * 0.5f).ToString("0.###", invariantCulture);
                sizeY = size.y.ToString("0.###", invariantCulture);
                sizeZ = string.Empty;
                blankInnerRadius = "0";
                break;

            case WorkpieceBlankShape.Tube:
            case WorkpieceBlankShape.HalfTube:
                sizeX = (Mathf.Min(size.x, size.z) * 0.5f).ToString("0.###", invariantCulture);
                sizeY = size.y.ToString("0.###", invariantCulture);
                sizeZ = string.Empty;
                blankInnerRadius = bootstrap.blankInnerRadiusMm.ToString("0.###", invariantCulture);
                break;

            case WorkpieceBlankShape.ImportedMesh:
                size = bootstrap.importedBlankScalePercent;
                sizeX = size.x.ToString("0.###", invariantCulture);
                sizeY = size.y.ToString("0.###", invariantCulture);
                sizeZ = size.z.ToString("0.###", invariantCulture);
                blankInnerRadius = "0";
                break;

            case WorkpieceBlankShape.Box:
            default:
                sizeX = size.x.ToString("0.###", invariantCulture);
                sizeY = size.y.ToString("0.###", invariantCulture);
                sizeZ = size.z.ToString("0.###", invariantCulture);
                blankInnerRadius = bootstrap.blankInnerRadiusMm.ToString("0.###", invariantCulture);
                break;
        }
    }
}
