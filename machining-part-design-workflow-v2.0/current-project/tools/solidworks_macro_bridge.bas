Attribute VB_Name = "SolidWorksMacroBridge"
Option Explicit

' Minimal SolidWorks internal VBA bridge.
' Edit these two constants before running inside SolidWorks.
Private Const SOURCE_FILE As String = "C:\Users\18015\Desktop\New folder1\有螺牙组件\DP90.11-01-01-22A.SLDPRT"
Private Const OUTPUT_FILE As String = "C:\Users\18015\Documents\ChatGPT\New project\working\DP90.11-01-01-22A\macro_bridge_probe.json"

Private Function JsonEscape(ByVal value As String) As String
    value = Replace(value, "\", "\\")
    value = Replace(value, """", "\""")
    value = Replace(value, vbCrLf, "\n")
    value = Replace(value, vbCr, "\n")
    value = Replace(value, vbLf, "\n")
    JsonEscape = value
End Function

Private Function JsonString(ByVal value As String) As String
    JsonString = """" & JsonEscape(value) & """"
End Function

Private Function TryReadProperty(ByVal obj As Object, ByVal propertyName As String, ByRef outValue As String) As Boolean
    On Error GoTo Failed
    Dim value As Variant
    value = CallByName(obj, propertyName, VbGet)
    outValue = CStr(value)
    TryReadProperty = True
    Exit Function
Failed:
    outValue = ""
    TryReadProperty = False
End Function

Private Function FeatureDefinitionJson(ByVal feature As Object, ByVal model As Object) As String
    On Error GoTo Failed
    Dim definition As Object
    Set definition = feature.GetDefinition
    If definition Is Nothing Then
        FeatureDefinitionJson = "{}"
        Exit Function
    End If

    On Error Resume Next
    definition.AccessSelections model, Nothing
    On Error GoTo Failed

    Dim fields As Variant
    fields = Array("Type", "HoleType", "Diameter", "Depth", "Depth2", "EndCondition", "EndCondition2", "ThreadType", "ThreadClass", "ThreadDepth", "ThreadPitch", "MajorDiameter", "MinorDiameter", "CosmeticThread")

    Dim pieces As String
    pieces = ""

    Dim i As Integer
    For i = LBound(fields) To UBound(fields)
        Dim textValue As String
        If TryReadProperty(definition, CStr(fields(i)), textValue) Then
            If Len(pieces) > 0 Then pieces = pieces & ","
            pieces = pieces & JsonString(CStr(fields(i))) & ":" & JsonString(textValue)
        End If
    Next i

    On Error Resume Next
    definition.ReleaseSelectionAccess
    On Error GoTo 0

    FeatureDefinitionJson = "{" & pieces & "}"
    Exit Function
Failed:
    FeatureDefinitionJson = "{}"
End Function

Private Sub WriteTextFile(ByVal path As String, ByVal content As String)
    Dim fso As Object
    Set fso = CreateObject("Scripting.FileSystemObject")
    Dim parent As String
    parent = fso.GetParentFolderName(path)
    If Len(parent) > 0 And Not fso.FolderExists(parent) Then
        fso.CreateFolder parent
    End If
    Dim file As Object
    Set file = fso.CreateTextFile(path, True, True)
    file.Write content
    file.Close
End Sub

Public Sub Main()
    On Error GoTo Failed
    Dim swApp As Object
    Set swApp = Application.SldWorks

    Dim errors As Long
    Dim warnings As Long
    Dim model As Object
    Set model = swApp.OpenDoc6(SOURCE_FILE, 1, 1, "", errors, warnings)

    Dim json As String
    json = "{"
    json = json & """schema_version"":""1.0"","
    json = json & """mode"":""solidworks_vba_macro_bridge"","
    json = json & """source_file"":" & JsonString(SOURCE_FILE) & ","
    json = json & """source_model_modified"":false,"
    json = json & """opendoc6_errors"":" & CStr(errors) & ","
    json = json & """opendoc6_warnings"":" & CStr(warnings) & ","

    If model Is Nothing Then
        json = json & """status"":""failed"","
        json = json & """message"":""OpenDoc6 returned null document object"","
        json = json & """features"":[]"
        json = json & "}"
        WriteTextFile OUTPUT_FILE, json
        Exit Sub
    End If

    json = json & """status"":""passed"","
    json = json & """document_title"":" & JsonString(model.GetTitle) & ","
    json = json & """features"":["

    Dim feature As Object
    Set feature = model.FirstFeature
    Dim first As Boolean
    first = True
    Do While Not feature Is Nothing
        If Not first Then json = json & ","
        first = False
        json = json & "{"
        json = json & """name"":" & JsonString(feature.Name) & ","
        json = json & """type"":" & JsonString(feature.GetTypeName2) & ","
        json = json & """parameters"":" & FeatureDefinitionJson(feature, model)
        json = json & "}"
        Set feature = feature.GetNextFeature
    Loop

    json = json & "]}"
    WriteTextFile OUTPUT_FILE, json
    swApp.CloseDoc model.GetTitle
    Exit Sub

Failed:
    Dim failedJson As String
    failedJson = "{""schema_version"":""1.0"",""mode"":""solidworks_vba_macro_bridge"",""status"":""failed"",""source_model_modified"":false,""message"":" & JsonString(Err.Description) & "}"
    WriteTextFile OUTPUT_FILE, failedJson
End Sub
