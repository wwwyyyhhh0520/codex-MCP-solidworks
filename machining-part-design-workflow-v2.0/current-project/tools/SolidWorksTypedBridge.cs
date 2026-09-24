using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class SolidWorksTypedBridge
{
    private static string Json(string value)
    {
        if (value == null) return "\"\"";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static string JsonNumber(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string BoxJson(object rawBox)
    {
        Array values = rawBox as Array;
        if (values == null || values.Length < 6) return "{}";
        try
        {
            string[] names = { "min_x", "min_y", "min_z", "max_x", "max_y", "max_z" };
            string output = "";
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) output += ",";
                output += Json(names[i]) + ":" + JsonNumber(Convert.ToDouble(values.GetValue(i)));
            }
            return "{" + output + "}";
        }
        catch { return "{}"; }
    }

    private static string BodiesJson(IPartDoc part)
    {
        try
        {
            Array bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as Array;
            if (bodies == null) return "[]";
            string output = "";
            foreach (object rawBody in bodies)
            {
                IBody2 body = rawBody as IBody2;
                if (body == null) continue;
                Array faces = null;
                try { faces = body.GetFaces() as Array; } catch { }
                if (output.Length > 0) output += ",";
                output += "{\"name\":" + Json(body.Name) + ",\"type\":\"solid\",\"face_count\":" + (faces == null ? "0" : faces.Length.ToString(CultureInfo.InvariantCulture)) + ",\"bounding_box\":" + BoxJson(body.GetBodyBox()) + "}";
            }
            return "[" + output + "]";
        }
        catch { return "[]"; }
    }

    private static string ViewMetricsJson(string orientation, string alias, View view)
    {
        string outline = "{}";
        try
        {
            Array values = view.GetOutline() as Array;
            if (values != null && values.Length >= 4)
            {
                double minX = Convert.ToDouble(values.GetValue(0));
                double minY = Convert.ToDouble(values.GetValue(1));
                double maxX = Convert.ToDouble(values.GetValue(2));
                double maxY = Convert.ToDouble(values.GetValue(3));
                outline = "{\"min_x\":" + JsonNumber(minX) + ",\"min_y\":" + JsonNumber(minY) + ",\"max_x\":" + JsonNumber(maxX) + ",\"max_y\":" + JsonNumber(maxY) + ",\"width\":" + JsonNumber(maxX - minX) + ",\"height\":" + JsonNumber(maxY - minY) + "}";
            }
        }
        catch { }
        int hiddenEdges = -1;
        try { hiddenEdges = view.GetHiddenEdgeCount(); } catch { }
        int visibleEdges = -1;
        int visibleFaces = -1;
        int circularEdges = -1;
        try
        {
            visibleEdges = view.GetVisibleEntityCount(null, (int)swViewEntityType_e.swViewEntityType_Edge);
            visibleFaces = view.GetVisibleEntityCount(null, (int)swViewEntityType_e.swViewEntityType_Face);
            Array edges = view.GetVisibleEntities(null, (int)swViewEntityType_e.swViewEntityType_Edge) as Array;
            circularEdges = 0;
            if (edges != null)
            {
                foreach (object rawEdge in edges)
                {
                    IEdge edge = rawEdge as IEdge;
                    ICurve curve = edge == null ? null : edge.GetCurve() as ICurve;
                    if (curve != null && curve.IsCircle()) circularEdges++;
                }
            }
        }
        catch { }
        return "{\"orientation\":" + Json(orientation) + ",\"inserted\":true,\"used_alias\":" + Json(alias) + ",\"outline\":" + outline + ",\"hidden_edge_count\":" + hiddenEdges.ToString(CultureInfo.InvariantCulture) + ",\"visible_edge_count\":" + visibleEdges.ToString(CultureInfo.InvariantCulture) + ",\"visible_face_count\":" + visibleFaces.ToString(CultureInfo.InvariantCulture) + ",\"visible_circular_edge_count\":" + circularEdges.ToString(CultureInfo.InvariantCulture) + "}";
    }

    private static string HoleCorrespondenceJson(View view, IFeature holeFeature)
    {
        int faceCount = 0;
        int correspondingCount = 0;
        try
        {
            Array faces = holeFeature.GetFaces() as Array;
            if (faces != null)
            {
                faceCount = faces.Length;
                foreach (object rawFace in faces)
                {
                    try
                    {
                        if (view.GetCorrespondingEntity(rawFace) != null) correspondingCount++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return "\"hole_feature_face_count\":" + faceCount.ToString(CultureInfo.InvariantCulture) + ",\"hole_corresponding_entity_count\":" + correspondingCount.ToString(CultureInfo.InvariantCulture);
    }

    private static string CylindricalFaceCorrespondenceJson(View view, IPartDoc part)
    {
        int cylindricalFaceCount = 0;
        int correspondingEntityCount = 0;
        int correspondingCircularEdgeCount = 0;
        int cylindricalBoundaryEdgeCount = 0;
        int correspondingBoundaryEdgeCount = 0;
        int correspondingBoundaryCircularEdgeCount = 0;
        string mappings = "";
        string cylinders = "";
        try
        {
            Array bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as Array;
            if (bodies != null)
            {
                foreach (object rawBody in bodies)
                {
                    IBody2 body = rawBody as IBody2;
                    Array faces = body == null ? null : body.GetFaces() as Array;
                    if (faces == null) continue;
                    foreach (object rawFace in faces)
                    {
                        IFace2 face = rawFace as IFace2;
                        ISurface surface = face == null ? null : face.GetSurface() as ISurface;
                        if (surface == null || !surface.IsCylinder()) continue;
                        cylindricalFaceCount++;
                        Array surfaceParams = surface.CylinderParams as Array;
                        double faceDiameter = 0.0;
                        double axisX = 0.0, axisY = 0.0, axisZ = 0.0;
                        if (surfaceParams != null && surfaceParams.Length >= 7)
                        {
                            faceDiameter = 2.0 * Convert.ToDouble(surfaceParams.GetValue(6));
                            axisX = Convert.ToDouble(surfaceParams.GetValue(3));
                            axisY = Convert.ToDouble(surfaceParams.GetValue(4));
                            axisZ = Convert.ToDouble(surfaceParams.GetValue(5));
                        }
                        Array faceBox = face.GetBox() as Array;
                        if (cylinders.Length > 0) cylinders += ",";
                        cylinders += "{\"diameter_m\":" + JsonNumber(faceDiameter)
                            + ",\"axis\":[" + JsonNumber(axisX) + "," + JsonNumber(axisY) + "," + JsonNumber(axisZ) + "]"
                            + ",\"box\":" + BoxJson(faceBox) + "}";
                        object corresponding = null;
                        try { corresponding = view.GetCorrespondingEntity(face); } catch { }
                        if (corresponding == null) continue;
                        correspondingEntityCount++;
                        IEdge edge = corresponding as IEdge;
                        ICurve curve = edge == null ? null : edge.GetCurve() as ICurve;
                        if (curve != null && curve.IsCircle()) correspondingCircularEdgeCount++;
                        Array boundaryEdges = face.GetEdges() as Array;
                        if (boundaryEdges == null) continue;
                        int boundaryEdgeCountForFace = boundaryEdges.Length;
                        foreach (object rawBoundaryEdge in boundaryEdges)
                        {
                            IEdge boundaryEdge = rawBoundaryEdge as IEdge;
                            if (boundaryEdge == null) continue;
                            cylindricalBoundaryEdgeCount++;
                            ICurve modelBoundaryCurve = boundaryEdge.GetCurve() as ICurve;
                            bool modelBoundaryIsCircle = modelBoundaryCurve != null && modelBoundaryCurve.IsCircle();
                            double modelBoundaryDiameter = 0.0;
                            if (modelBoundaryIsCircle)
                            {
                                Array modelCircleParams = modelBoundaryCurve.CircleParams as Array;
                                if (modelCircleParams != null && modelCircleParams.Length >= 7)
                                    modelBoundaryDiameter = 2.0 * Convert.ToDouble(modelCircleParams.GetValue(6));
                            }
                            object mappedBoundary = null;
                            try { mappedBoundary = view.GetCorrespondingEntity(boundaryEdge); } catch { }
                            IEdge mappedEdge = mappedBoundary as IEdge;
                            if (mappedEdge == null) continue;
                            correspondingBoundaryEdgeCount++;
                            ICurve mappedCurve = mappedEdge.GetCurve() as ICurve;
                            bool mappedIsCircle = mappedCurve != null && mappedCurve.IsCircle();
                            if (mappedIsCircle) correspondingBoundaryCircularEdgeCount++;
                            double boundaryDiameter = 0.0;
                            if (mappedIsCircle)
                            {
                                Array circleParams = mappedCurve.CircleParams as Array;
                                if (circleParams != null && circleParams.Length >= 7)
                                    boundaryDiameter = 2.0 * Convert.ToDouble(circleParams.GetValue(6));
                            }
                            if (mappings.Length > 0) mappings += ",";
                            mappings += "{\"model_cylindrical_diameter_m\":" + JsonNumber(faceDiameter)
                                + ",\"model_boundary_diameter_m\":" + JsonNumber(modelBoundaryDiameter)
                                + ",\"model_boundary_is_circle\":" + (modelBoundaryIsCircle ? "true" : "false")
                                + ",\"drawing_boundary_diameter_m\":" + JsonNumber(boundaryDiameter)
                                + ",\"drawing_boundary_is_circle\":" + (mappedIsCircle ? "true" : "false")
                                + ",\"model_boundary_edge_count\":" + boundaryEdgeCountForFace.ToString(CultureInfo.InvariantCulture) + "}";
                        }
                    }
                }
            }
        }
        catch { }
        return "\"cylindrical_face_count\":" + cylindricalFaceCount.ToString(CultureInfo.InvariantCulture)
            + ",\"corresponding_entity_count\":" + correspondingEntityCount.ToString(CultureInfo.InvariantCulture)
            + ",\"corresponding_circular_edge_count\":" + correspondingCircularEdgeCount.ToString(CultureInfo.InvariantCulture)
            + ",\"cylindrical_boundary_edge_count\":" + cylindricalBoundaryEdgeCount.ToString(CultureInfo.InvariantCulture)
            + ",\"corresponding_boundary_edge_count\":" + correspondingBoundaryEdgeCount.ToString(CultureInfo.InvariantCulture)
            + ",\"corresponding_boundary_circular_edge_count\":" + correspondingBoundaryCircularEdgeCount.ToString(CultureInfo.InvariantCulture)
            + ",\"cylinders\":[" + cylinders + "]"
            + ",\"mappings\":[" + mappings + "]";
    }

    private static IVertex ExtremeVisibleVertex(View view, int axis, bool minimum)
    {
        IVertex selected = null;
        double selectedValue = 0.0;
        try
        {
            Array edges = view.GetVisibleEntities(null, (int)swViewEntityType_e.swViewEntityType_Edge) as Array;
            if (edges == null) return null;
            foreach (object rawEdge in edges)
            {
                IEdge edge = rawEdge as IEdge;
                if (edge == null) continue;
                IVertex[] vertices = { edge.GetStartVertex() as IVertex, edge.GetEndVertex() as IVertex };
                foreach (IVertex vertex in vertices)
                {
                    if (vertex == null) continue;
                    Array point = vertex.GetPoint() as Array;
                    if (point == null || point.Length <= axis) continue;
                    double value = Convert.ToDouble(point.GetValue(axis));
                    if (selected == null || (minimum ? value < selectedValue : value > selectedValue))
                    {
                        selected = vertex;
                        selectedValue = value;
                    }
                }
            }
        }
        catch { }
        return selected;
    }

    private static string AddOverallDimension(View view, IModelDoc2 drawing, int axis, bool horizontal, double x, double y)
    {
        try
        {
            IVertex low = ExtremeVisibleVertex(view, axis, true);
            IVertex high = ExtremeVisibleVertex(view, axis, false);
            if (low == null || high == null || Object.ReferenceEquals(low, high)) return "{\"created\":false,\"reason\":\"extreme_vertices_not_available\"}";
            drawing.ClearSelection2(true);
            if (!view.SelectEntity(low, false) || !view.SelectEntity(high, true)) return "{\"created\":false,\"reason\":\"drawing_vertex_selection_failed\"}";
            object dimension = horizontal ? drawing.AddHorizontalDimension2(x, y, 0.0) : drawing.AddVerticalDimension2(x, y, 0.0);
            drawing.ClearSelection2(true);
            return "{\"created\":" + (dimension != null ? "true" : "false") + ",\"axis\":" + axis.ToString(CultureInfo.InvariantCulture) + ",\"kind\":" + Json(horizontal ? "horizontal" : "vertical") + "}";
        }
        catch (Exception ex)
        {
            try { drawing.ClearSelection2(true); } catch { }
            return "{\"created\":false,\"reason\":" + Json(ex.Message) + "}";
        }
    }

    private static string HoleGeometryJson(IFeature holeFeature)
    {
        string cylinders = "";
        string circles = "";
        int faceCount = 0;
        try
        {
            Array faces = holeFeature.GetFaces() as Array;
            if (faces != null)
            {
                faceCount = faces.Length;
                foreach (object rawFace in faces)
                {
                    IFace face = rawFace as IFace;
                    ISurface surface = face == null ? null : face.GetSurface() as ISurface;
                    if (surface != null && surface.IsCylinder())
                    {
                        Array parameters = surface.CylinderParams as Array;
                        if (parameters != null && parameters.Length >= 7)
                        {
                            double axisX = Convert.ToDouble(parameters.GetValue(3));
                            double axisY = Convert.ToDouble(parameters.GetValue(4));
                            double axisZ = Convert.ToDouble(parameters.GetValue(5));
                            double[] axes = { Math.Abs(axisX), Math.Abs(axisY), Math.Abs(axisZ) };
                            int dominantAxis = axes[0] >= axes[1] && axes[0] >= axes[2] ? 0 : (axes[1] >= axes[2] ? 1 : 2);
                            bool axisAligned = axes[dominantAxis] >= 0.999999;
                            string axialExtent = "null";
                            if (axisAligned)
                            {
                                Array box = face.GetBox() as Array;
                                if (box != null && box.Length >= 6)
                                {
                                    double low = Convert.ToDouble(box.GetValue(dominantAxis));
                                    double high = Convert.ToDouble(box.GetValue(dominantAxis + 3));
                                    axialExtent = JsonNumber(Math.Abs(high - low));
                                }
                            }
                            if (cylinders.Length > 0) cylinders += ",";
                            cylinders += "{\"radius_m\":" + JsonNumber(Convert.ToDouble(parameters.GetValue(6))) + ",\"diameter_m\":" + JsonNumber(2.0 * Convert.ToDouble(parameters.GetValue(6))) + ",\"axis\":[" + JsonNumber(axisX) + "," + JsonNumber(axisY) + "," + JsonNumber(axisZ) + "],\"axis_aligned\":" + (axisAligned ? "true" : "false") + ",\"axial_extent_m\":" + axialExtent + ",\"source\":\"HoleWzd.GetFaces().GetSurface().CylinderParams[6]; IFace.GetBox()\"}";
                        }
                    }
                    Array edges = face == null ? null : face.GetEdges() as Array;
                    if (edges == null) continue;
                    foreach (object rawEdge in edges)
                    {
                        IEdge edge = rawEdge as IEdge;
                        ICurve curve = edge == null ? null : edge.GetCurve() as ICurve;
                        if (curve == null || !curve.IsCircle()) continue;
                        Array parameters = curve.CircleParams as Array;
                        if (parameters == null || parameters.Length < 7) continue;
                        if (circles.Length > 0) circles += ",";
                        circles += "{\"radius_m\":" + JsonNumber(Convert.ToDouble(parameters.GetValue(6))) + ",\"diameter_m\":" + JsonNumber(2.0 * Convert.ToDouble(parameters.GetValue(6))) + ",\"source\":\"HoleWzd.GetFaces().GetEdges().GetCurve().CircleParams[6]\"}";
                    }
                }
            }
        }
        catch { }
        return "{\"feature_face_count\":" + faceCount.ToString(CultureInfo.InvariantCulture) + ",\"cylindrical_faces\":[" + cylinders + "],\"circular_edges\":[" + circles + "]}";
    }

    private static string CustomPropertiesJson(ICustomPropertyManager manager)
    {
        if (manager == null) return "{}";
        try
        {
            Array names = manager.GetNames() as Array;
            if (names == null) return "{}";
            string output = "";
            foreach (object rawName in names)
            {
                string name = Convert.ToString(rawName);
                string value = "";
                string resolved = "";
                bool wasResolved = false;
                bool linked = false;
                try { manager.Get6(name, false, out value, out resolved, out wasResolved, out linked); }
                catch { continue; }
                if (output.Length > 0) output += ",";
                output += Json(name) + ":{\"raw_value\":" + Json(value) + ",\"resolved_value\":" + Json(resolved) + ",\"was_resolved\":" + (wasResolved ? "true" : "false") + ",\"linked\":" + (linked ? "true" : "false") + "}";
            }
            return "{" + output + "}";
        }
        catch { return "{}"; }
    }

    private static string ModelPropertiesJson(IModelDoc2 model)
    {
        try
        {
            ICustomPropertyManager documentProperties = model.Extension.get_CustomPropertyManager("") as ICustomPropertyManager;
            IConfiguration configuration = model.GetActiveConfiguration() as IConfiguration;
            ICustomPropertyManager configurationProperties = configuration == null ? null : configuration.CustomPropertyManager as ICustomPropertyManager;
            return "{\"document\":" + CustomPropertiesJson(documentProperties) + ",\"active_configuration\":" + CustomPropertiesJson(configurationProperties) + "}";
        }
        catch { return "{\"document\":{},\"active_configuration\":{}}"; }
    }

    private static string PmiJson(IModelDoc2 model)
    {
        try
        {
            Array annotations = model.Extension.GetAnnotations() as Array;
            if (annotations == null) return "{\"annotation_count\":0,\"items\":[]}";
            string items = "";
            foreach (object rawAnnotation in annotations)
            {
                IAnnotation annotation = rawAnnotation as IAnnotation;
                if (annotation == null) continue;
                object specific = annotation.GetSpecificAnnotation();
                IDisplayDimension displayDimension = specific as IDisplayDimension;
                string dimensionJson = "null";
                if (displayDimension != null)
                {
                    try
                    {
                        IDimension dimension = displayDimension.GetDimension() as IDimension;
                        if (dimension != null)
                        {
                            Array values = dimension.GetToleranceValues() as Array;
                            string toleranceValues = "";
                            if (values != null)
                            {
                                foreach (object value in values)
                                {
                                    if (toleranceValues.Length > 0) toleranceValues += ",";
                                    toleranceValues += JsonNumber(Convert.ToDouble(value));
                                }
                            }
                            dimensionJson = "{\"name\":" + Json(dimension.Name) + ",\"system_value_m\":" + JsonNumber(dimension.SystemValue) + ",\"tolerance_type\":" + dimension.GetToleranceType().ToString(CultureInfo.InvariantCulture) + ",\"tolerance_values\":[" + toleranceValues + "]}";
                        }
                    }
                    catch { }
                }
                if (items.Length > 0) items += ",";
                items += "{\"name\":" + Json(annotation.GetName()) + ",\"annotation_type\":" + annotation.GetType().ToString(CultureInfo.InvariantCulture) + ",\"is_dimxpert\":" + (annotation.IsDimXpert() ? "true" : "false") + ",\"dimension\":" + dimensionJson + "}";
            }
            return "{\"annotation_count\":" + annotations.Length.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + items + "]}";
        }
        catch { return "{\"annotation_count\":-1,\"items\":[]}"; }
    }

    private static string DrawingTablesJson(IDrawingDoc drawing)
    {
        try
        {
            string tables = "";
            View view = drawing.GetFirstView() as View;
            while (view != null)
            {
                Array annotations = view.GetTableAnnotations() as Array;
                if (annotations != null)
                {
                    foreach (object rawTable in annotations)
                    {
                        ITableAnnotation table = rawTable as ITableAnnotation;
                        if (table == null) continue;
                        int rows = Math.Min(table.RowCount, 20);
                        int columns = Math.Min(table.ColumnCount, 20);
                        string cells = "";
                        for (int row = 0; row < rows; row++)
                        {
                            for (int column = 0; column < columns; column++)
                            {
                                string text = "";
                                try { text = table.DisplayedText2[row, column, true]; } catch { }
                                if (cells.Length > 0) cells += ",";
                                cells += "{\"row\":" + row.ToString(CultureInfo.InvariantCulture) + ",\"column\":" + column.ToString(CultureInfo.InvariantCulture) + ",\"text\":" + Json(text) + "}";
                            }
                        }
                        if (tables.Length > 0) tables += ",";
                        tables += "{\"type\":" + Json(rawTable.GetType().FullName) + ",\"is_general_tolerance_table\":" + (rawTable is IGeneralToleranceTableAnnotation ? "true" : "false") + ",\"row_count\":" + table.RowCount.ToString(CultureInfo.InvariantCulture) + ",\"column_count\":" + table.ColumnCount.ToString(CultureInfo.InvariantCulture) + ",\"cells\":[" + cells + "]}";
                    }
                }
                view = view.GetNextView() as View;
            }
            return "{\"table_count\":" + (tables.Length == 0 ? "0" : "1") + ",\"tables\":[" + tables + "]}";
        }
        catch { return "{\"table_count\":-1,\"tables\":[]}"; }
    }

    private static string DrawingNotesJson(IDrawingDoc drawing)
    {
        try
        {
            string notes = "";
            int noteCount = 0;
            View view = drawing.GetFirstView() as View;
            while (view != null)
            {
                Array rawNotes = view.GetNotes() as Array;
                if (rawNotes != null)
                {
                    foreach (object rawNote in rawNotes)
                    {
                        INote note = rawNote as INote;
                        if (note == null || noteCount >= 200) continue;
                        string text = "";
                        try { text = note.GetText(); } catch { }
                        if (notes.Length > 0) notes += ",";
                        notes += "{\"text\":" + Json(text) + "}";
                        noteCount++;
                    }
                }
                view = view.GetNextView() as View;
            }
            return "{\"note_count\":" + noteCount.ToString(CultureInfo.InvariantCulture) + ",\"notes\":[" + notes + "]}";
        }
        catch { return "{\"note_count\":-1,\"notes\":[]}"; }
    }

    private static string DefinitionJson(IFeature feature, IModelDoc2 model)
    {
        try
        {
            object definition = feature.GetDefinition();
            if (definition == null) return "{}";
            if (feature.GetTypeName2() == "HoleWzd")
            {
                IWizardHoleFeatureData2 hole = definition as IWizardHoleFeatureData2;
                if (hole == null) return "{}";
                string values = "";
                try { hole.AccessSelections(model, null); } catch { }
                Action<string, object> append = (name, value) =>
                {
                    if (values.Length > 0) values += ",";
                    values += Json(name) + ":" + Json(Convert.ToString(value));
                };
                try { append("Type", hole.Type); } catch { }
                try { append("Standard", hole.Standard); } catch { }
                try { append("FastenerType", hole.FastenerType); } catch { }
                try { append("FastenerSize", hole.FastenerSize); } catch { }
                try { append("HoleDiameter", hole.HoleDiameter); } catch { }
                try { append("HoleDepth", hole.HoleDepth); } catch { }
                try { append("EndCondition", hole.EndCondition); } catch { }
                try { append("CounterBoreDiameter", hole.CounterBoreDiameter); } catch { }
                try { append("CounterBoreDepth", hole.CounterBoreDepth); } catch { }
                try { append("CounterSinkDiameter", hole.CounterSinkDiameter); } catch { }
                try { append("CounterSinkAngle", hole.CounterSinkAngle); } catch { }
                try { append("ThreadDiameter", hole.ThreadDiameter); } catch { }
                try { append("ThreadDepth", hole.ThreadDepth); } catch { }
                try { append("ThreadClass", hole.ThreadClass); } catch { }
                try { append("TapType", hole.TapType); } catch { }
                try { hole.ReleaseSelectionAccess(); } catch { }
                return "{" + values + "}";
            }
            string[] names = { "Type", "HoleType", "Diameter", "Depth", "Depth2", "EndCondition", "EndCondition2", "ThreadType", "ThreadClass", "ThreadDepth", "ThreadPitch", "MajorDiameter", "MinorDiameter", "CosmeticThread" };
            string output = "";
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = definition.GetType().GetProperty(name);
                    if (property == null) continue;
                    object value = property.GetValue(definition, null);
                    if (output.Length > 0) output += ",";
                    output += Json(name) + ":" + Json(Convert.ToString(value));
                }
                catch { }
            }
            return "{" + output + "}";
        }
        catch { return "{}"; }
    }

    public static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 5)
        {
            Console.Error.WriteLine("Usage: SolidWorksTypedBridge <source.sldprt> <output.json> [drawing-template.drwdot] [view-alias] [preview.slddrw]");
            return 64;
        }

        string source = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        ISldWorks app = null;
        IModelDoc2 model = null;
        IModelDoc2 temporaryDrawing = null;
        try
        {
            try { app = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
            catch { app = (ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application")); }
            app.Visible = true;

            int errors = 0;
            int warnings = 0;
            model = app.OpenDoc6(source, (int)swDocumentTypes_e.swDocPART,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                "", ref errors, ref warnings);
            if (model == null)
            {
                File.WriteAllText(output, "{\"schema_version\":\"1.0\",\"mode\":\"solidworks_typed_bridge\",\"status\":\"failed\",\"source_model_modified\":false,\"opendoc6_errors\":" + errors + ",\"opendoc6_warnings\":" + warnings + ",\"features\":[]}");
                return 2;
            }

            IPartDoc part = model as IPartDoc;
            string boundingBox = part == null ? "{}" : BoxJson(part.GetPartBox(true));
            string bodies = part == null ? "[]" : BodiesJson(part);
            string customProperties = ModelPropertiesJson(model);
            string pmi = PmiJson(model);
            IFeature holeFeature = null;
            IFeature featureCursor = (IFeature)model.FirstFeature();
            while (featureCursor != null)
            {
                if (featureCursor.GetTypeName2() == "HoleWzd") { holeFeature = featureCursor; break; }
                featureCursor = (IFeature)featureCursor.GetNextFeature();
            }
            string holeGeometry = holeFeature == null ? "{}" : HoleGeometryJson(holeFeature);
            string holeGeometryByFeature = "";
            IFeature holeCursor = (IFeature)model.FirstFeature();
            while (holeCursor != null)
            {
                if (holeCursor.GetTypeName2() == "HoleWzd")
                {
                    if (holeGeometryByFeature.Length > 0) holeGeometryByFeature += ",";
                    holeGeometryByFeature += Json(holeCursor.Name) + ":" + HoleGeometryJson(holeCursor);
                }
                holeCursor = (IFeature)holeCursor.GetNextFeature();
            }
            string drawingProbe = "{\"requested\":false}";
            if (args.Length >= 3)
            {
                string template = Path.GetFullPath(args[2]);
                try
                {
                    temporaryDrawing = app.NewDocument(template, 0, 0.0, 0.0) as IModelDoc2;
                    IDrawingDoc drawing = temporaryDrawing as IDrawingDoc;
                    string requestedAlias = args.Length >= 4 ? args[3] : "*Front";
                    if (requestedAlias == "--scan-template")
                    {
                        string tables = drawing == null ? "{\"table_count\":-1,\"tables\":[]}" : DrawingTablesJson(drawing);
                        string notes = drawing == null ? "{\"note_count\":-1,\"notes\":[]}" : DrawingNotesJson(drawing);
                        drawingProbe = "{\"requested\":true,\"template_scan_mode\":true,\"template_path\":" + Json(template) + ",\"drawing_created\":" + (drawing != null ? "true" : "false") + ",\"template_tables\":" + tables + ",\"template_notes\":" + notes + "}";
                    }
                    else if (requestedAlias == "--compare-views")
                    {
                        string[] orientations = { "front", "top", "right" };
                        string[][] groups = {
                            new string[] { "*Front", "*前视", "*前视图" },
                            new string[] { "*Top", "*上视", "*上视图" },
                            new string[] { "*Right", "*右视", "*右视图" }
                        };
                        string candidates = "";
                        for (int i = 0; i < orientations.Length; i++)
                        {
                            View candidate = null;
                            string usedAlias = "";
                            foreach (string alias in groups[i])
                            {
                                if (drawing == null) continue;
                                candidate = drawing.CreateDrawViewFromModelView3(source, alias, 0.08 + i * 0.10, 0.15, 0.0);
                                if (candidate != null) { usedAlias = alias; break; }
                            }
                            if (candidates.Length > 0) candidates += ",";
                            candidates += candidate == null
                                ? "{\"orientation\":" + Json(orientations[i]) + ",\"inserted\":false}"
                                : ViewMetricsJson(orientations[i], usedAlias, candidate).TrimEnd('}')
                                    + "," + CylindricalFaceCorrespondenceJson(candidate, part)
                                    + (holeFeature == null ? "" : "," + HoleCorrespondenceJson(candidate, holeFeature)) + "}";
                        }
                        drawingProbe = "{\"requested\":true,\"comparison_mode\":true,\"template_path\":" + Json(template) + ",\"drawing_created\":" + (drawing != null ? "true" : "false") + ",\"view_candidates\":[" + candidates + "]}";
                    }
                    else if ((requestedAlias == "--render-preview" || requestedAlias == "--render-dimension-preview") && args.Length == 5)
                    {
                        string[][] groups = {
                            new string[] { "front", "*Front", "*前视", "*前视图", "0.14", "0.25" },
                            new string[] { "top", "*Top", "*上视", "*上视图", "0.31", "0.25" },
                            new string[] { "right", "*Right", "*右视", "*右视图", "0.14", "0.10" }
                        };
                        string views = "";
                        View frontPreview = null;
                        View rightPreview = null;
                        for (int i = 0; i < groups.Length; i++)
                        {
                            View previewView = null;
                            string usedAlias = "";
                            for (int aliasIndex = 1; aliasIndex <= 3; aliasIndex++)
                            {
                                previewView = drawing == null ? null : drawing.CreateDrawViewFromModelView3(
                                    source, groups[i][aliasIndex],
                                    Convert.ToDouble(groups[i][4], CultureInfo.InvariantCulture),
                                    Convert.ToDouble(groups[i][5], CultureInfo.InvariantCulture), 0.0);
                                if (previewView != null) { usedAlias = groups[i][aliasIndex]; break; }
                            }
                            if (views.Length > 0) views += ",";
                            views += previewView == null
                                ? "{\"orientation\":" + Json(groups[i][0]) + ",\"inserted\":false}"
                                : ViewMetricsJson(groups[i][0], usedAlias, previewView);
                            if (groups[i][0] == "front") frontPreview = previewView;
                            if (groups[i][0] == "right") rightPreview = previewView;
                        }
                        string dimensions = "[]";
                        if (requestedAlias == "--render-dimension-preview")
                        {
                            string xDimension = frontPreview == null ? "{\"created\":false,\"reason\":\"front_view_not_inserted\"}" : AddOverallDimension(frontPreview, temporaryDrawing, 0, true, 0.14, 0.34);
                            string yDimension = frontPreview == null ? "{\"created\":false,\"reason\":\"front_view_not_inserted\"}" : AddOverallDimension(frontPreview, temporaryDrawing, 1, false, 0.10, 0.25);
                            string zDimension = rightPreview == null ? "{\"created\":false,\"reason\":\"right_view_not_inserted\"}" : AddOverallDimension(rightPreview, temporaryDrawing, 2, true, 0.14, 0.01);
                            dimensions = "[" + xDimension + "," + yDimension + "," + zDimension + "]";
                        }
                        string previewPath = Path.GetFullPath(args[4]);
                        string previewDirectory = Path.GetDirectoryName(previewPath);
                        if (!String.IsNullOrEmpty(previewDirectory)) Directory.CreateDirectory(previewDirectory);
                        int saveResult = temporaryDrawing == null ? 0 : temporaryDrawing.SaveAs3(previewPath, 0, 1);
                        bool saved = saveResult == 0 && File.Exists(previewPath);
                        drawingProbe = "{\"requested\":true,\"preview_render_mode\":true,\"dimension_preview_mode\":" + (requestedAlias == "--render-dimension-preview" ? "true" : "false") + ",\"template_path\":" + Json(template) + ",\"drawing_created\":" + (drawing != null ? "true" : "false") + ",\"preview_drawing_path\":" + Json(previewPath) + ",\"save_result\":" + saveResult.ToString(CultureInfo.InvariantCulture) + ",\"preview_drawing_saved\":" + (saved ? "true" : "false") + ",\"dimensions\":" + dimensions + ",\"views\":[" + views + "]}";
                    }
                    else
                    {
                    string[] aliases = { requestedAlias, "*Front", "*前视", "*前视图", "*Top", "*上视", "*上视图", "*Right", "*右视", "*右视图" };
                    View view = null;
                    string usedAlias = "";
                    string triedAliases = "";
                    foreach (string alias in aliases)
                    {
                        if (triedAliases.Contains(Json(alias))) continue;
                        if (triedAliases.Length > 0) triedAliases += ",";
                        triedAliases += Json(alias);
                        if (drawing == null) continue;
                        view = drawing.CreateDrawViewFromModelView3(source, alias, 0.18, 0.15, 0.0);
                        if (view != null) { usedAlias = alias; break; }
                    }
                    drawingProbe = "{\"requested\":true,\"template_path\":" + Json(template) + ",\"drawing_created\":" + (drawing != null ? "true" : "false") + ",\"base_view_inserted\":" + (view != null ? "true" : "false") + ",\"requested_orientation\":" + Json(requestedAlias) + ",\"used_orientation\":" + Json(usedAlias) + ",\"tried_orientations\":[" + triedAliases + "]}";
                    }
                }
                catch (Exception ex)
                {
                    drawingProbe = "{\"requested\":true,\"template_path\":" + Json(template) + ",\"drawing_created\":false,\"base_view_inserted\":false,\"message\":" + Json(ex.Message) + "}";
                }
            }

            string features = "";
            IFeature feature = (IFeature)model.FirstFeature();
            while (feature != null)
            {
                if (features.Length > 0) features += ",";
                features += "{\"name\":" + Json(feature.Name) + ",\"type\":" + Json(feature.GetTypeName2()) + ",\"parameters\":" + DefinitionJson(feature, model) + "}";
                feature = (IFeature)feature.GetNextFeature();
            }
            string report = "{\"schema_version\":\"1.0\",\"mode\":\"solidworks_typed_bridge\",\"status\":\"passed\",\"source_file\":" + Json(source) + ",\"source_model_modified\":false,\"drawing_saved\":false,\"opendoc6_errors\":" + errors + ",\"opendoc6_warnings\":" + warnings + ",\"document_title\":" + Json(model.GetTitle()) + ",\"custom_properties\":" + customProperties + ",\"pmi\":" + pmi + ",\"bounding_box\":" + boundingBox + ",\"bodies\":" + bodies + ",\"hole_geometry\":" + holeGeometry + ",\"hole_geometry_by_feature\":{" + holeGeometryByFeature + "},\"drawing_probe\":" + drawingProbe + ",\"features\":[" + features + "]}";
            File.WriteAllText(output, report);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, "{\"schema_version\":\"1.0\",\"mode\":\"solidworks_typed_bridge\",\"status\":\"failed\",\"source_model_modified\":false,\"message\":" + Json(ex.Message) + "}");
            return 2;
        }
        finally
        {
            if (app != null && temporaryDrawing != null)
            {
                try { app.CloseDoc(temporaryDrawing.GetTitle()); }
                catch { }
            }
            if (app != null && model != null)
            {
                try { app.CloseDoc(model.GetTitle()); }
                catch { }
            }
        }
    }
}
