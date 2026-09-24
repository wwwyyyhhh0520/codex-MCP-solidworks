from pathlib import Path
import pythoncom
import win32com.client


DOC_TYPES = {
    ".SLDPRT": 1,
    ".SLDASM": 2,
    ".SLDDRW": 3,
}


def value_or_call(obj, name, default=""):
    try:
        v = getattr(obj, name)
        if callable(v):
            v = v()
        return v
    except Exception:
        return default


def open_document(sw, path, doc_type=None, silent=True, read_only=False, raise_on_error=True, **kwargs):
    path = str(path)
    if doc_type is None:
        doc_type = DOC_TYPES.get(Path(path).suffix.upper(), 1)

    errors = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    warnings = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)

    # swOpenDocOptions_Silent=1, swOpenDocOptions_ReadOnly=2
    opts = 0
    if silent:
        opts |= 1
    if read_only:
        opts |= 2

    try:
        doc = sw.OpenDoc6(path, doc_type, opts, "", errors, warnings)
    except Exception as e:
        if raise_on_error:
            raise
        return None

    if doc is None:
        if raise_on_error:
            raise RuntimeError(f"OPEN_DOC_FAILED={path}|ERRORS={errors.value}|WARNINGS={warnings.value}")
        return None

    try:
        sw.ActivateDoc3(Path(path).name, False, 0, errors)
    except Exception:
        pass

    return doc


def iter_feature_tree(model):
    try:
        feature = model.FirstFeature()
    except Exception:
        feature = None

    while feature is not None:
        yield feature

        try:
            child = feature.GetFirstSubFeature()
        except Exception:
            child = None

        while child is not None:
            yield child
            try:
                child = child.GetNextSubFeature()
            except Exception:
                child = None

        try:
            feature = feature.GetNextFeature()
        except Exception:
            feature = None
