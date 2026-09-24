from __future__ import annotations

from sw_com_session import connect_solidworks_session


def connect_solidworks(visible=True, allow_create=None):
    result = connect_solidworks_session(visible=visible, allow_create=allow_create)
    return result.sw, result.created_new


def get_solidworks(visible=True, allow_create=None):
    result = connect_solidworks_session(visible=visible, allow_create=allow_create)
    return result.sw
