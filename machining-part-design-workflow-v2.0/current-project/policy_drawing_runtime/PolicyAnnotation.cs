using SolidWorks.Interop.sldworks;
sealed record PolicyAnnotation(string Id,string Kind,string Role,DisplayDimension Display,double[][] Endpoints,string Text,string Orientation,object Lineage);
