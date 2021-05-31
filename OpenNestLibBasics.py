import Rhino
import scriptcontext
import rhinoscriptsyntax as rs
import System
import clr

from System.Collections.Generic import List
from System.Threading.Tasks import Task

clr.AddReference("OpenNestLib")

import OpenNestLib


tolerance = scriptcontext.doc.ModelAbsoluteTolerance
plane_xy = Rhino.Geometry.Plane.WorldXY

def polyline_filter(rhino_object, geometry, component_index):
    if geometry.IsClosed and geometry.IsInPlane(plane_xy, tolerance):
        if isinstance(geometry, Rhino.Geometry.Polyline) or isinstance(geometry, Rhino.Geometry.PolylineCurve):
            return True
    return False

class OpenNestContext(OpenNestLib.OpenNestRhinoContext):

    def __init__(self):
        self.run = False
        self.reset = False
        self.staticSolver = False
        
        self.scalesInv = System.Array.CreateInstance(Rhino.Geometry.Transform, 0)
        self.scales = System.Array.CreateInstance(Rhino.Geometry.Transform, 0)
        self.orient = System.Array.CreateInstance(Rhino.Geometry.Transform, 0)
        
        self.sheetsRhino = System.Array.CreateInstance(Rhino.Geometry.Polyline, 0)
        self.id = System.Array.CreateInstance(int, 0)
        self.transforms = System.Array.CreateInstance(Rhino.Geometry.Transform, 0)
        
        #self.spacing = 10.0
        #self.placementType = 1
        #self.tolerance = 0.1
        #self.rotations = 4
        #self.iterations = 10
        #self.x = 0 # removed
        self.n = 0
        #self.seed = 2
        self.oneSheet = False # unused ?
        #self.cp = 0.01

    def GetNestingSettings(self):
        # Parameters
        go = Rhino.Input.Custom.GetOption()
        go.SetCommandPrompt("OpenNest: Set nesting settings")
        
        optIterations = Rhino.Input.Custom.OptionInteger(1, 1, 100)
        #optSpacing = Rhino.Input.Custom.OptionDouble(0.01, 0.001, 1E10)
        optSpacing = Rhino.Input.Custom.OptionDouble(1.0, True, 0.001)
        optPlacement = Rhino.Input.Custom.OptionInteger(1, 0, 4)
        optTolerance = Rhino.Input.Custom.OptionDouble(0.1, 0.01, 10)
        optRotations = Rhino.Input.Custom.OptionInteger(4, 1, 360)
        optSeed = Rhino.Input.Custom.OptionInteger(0, 0, 100)
        optClosestObjects = Rhino.Input.Custom.OptionDouble(0.05, 0, 100)

        go.AddOptionInteger("Iterations", optIterations)
        go.AddOptionDouble("Spacing", optSpacing, "Spacing between sheets if only one is selected")
        go.AddOptionInteger("Placement", optPlacement, "Placement side to start nesting from")
        go.AddOptionDouble("Tolerance", optTolerance, "Gap size between nested parts")
        go.AddOptionInteger("Rotations", optRotations, "Number of rotation steps, use 1 for no rotation")
        go.AddOptionInteger("Seed", optSeed, "Random seed")
        go.AddOptionDouble("ClosestObjects", optClosestObjects)

        while True:
            if scriptcontext.escape_test(False): return False
            
            get_rc = go.Get()
            if go.CommandResult() != Rhino.Commands.Result.Success:
                break

        self.iterations = optIterations.CurrentValue
        self.spacing = optSpacing.CurrentValue
        self.placementType = optPlacement.CurrentValue
        self.tolerance = optTolerance.CurrentValue
        self.rotations = optRotations.CurrentValue
        self.seed = optSeed.CurrentValue
        #self.spacing *= (1 / tolerance)
        self.cp = optClosestObjects.CurrentValue
        
        print "optSpacing.CurrentValue = ", self.spacing
        return True

    def GetSheets(self):
        '''prompts for sheets to nest into'''
    
        message = "OpenNest: Select polylines for sheets"
        obj_ids = rs.GetObjects(message, 4, True, True, False, None, 1, 0, polyline_filter)
        if not obj_ids: return
        
        sheets = List[Rhino.Geometry.Polyline]()
        
        for obj_id in obj_ids:
            curve = rs.coercecurve(obj_id, -1, True)
            rc, polyline = curve.TryGetPolyline()
            if rc: 
                sheets.Add(polyline)
            else:
                # TODO: convert sheet curve to polyline
                pass
        
        print "OpenNest: {} sheets selected".format(sheets.Count)
        
        if sheets.Count == 0: return
        
        if sheets.Count == 1:
            
            # get width of sheet
            p0 = sheets[0].BoundingBox.PointAt(0.0, 0.0, 0.0)
            p1 = sheets[0].BoundingBox.PointAt(1.0, 0.0, 0.0)
            distance = p0.DistanceTo(p1)
            
            # make 99 more sheets along x-axis
            for i in xrange(1, 99):
                sheet = sheets[0].Duplicate()
                offset = (distance + self.spacing) * i 
                xform = Rhino.Geometry.Transform.Translation(offset, 0, 0)
                sheet.Transform(xform)
                #scriptcontext.doc.Objects.AddPolyline(sheet)
                sheets.Add(sheet)
        else:
            # why ?
            #self.x = 0
            pass
        
        return sheets

    def GetObjectsToNest(self):
        '''Select Objects To Nest'''
        geometryFilter = Rhino.DocObjects.ObjectType.Annotation | \
                         Rhino.DocObjects.ObjectType.TextDot | \
                         Rhino.DocObjects.ObjectType.Point | \
                         Rhino.DocObjects.ObjectType.Curve | \
                         Rhino.DocObjects.ObjectType.Surface | \
                         Rhino.DocObjects.ObjectType.PolysrfFilter | \
                         Rhino.DocObjects.ObjectType.Mesh
        
        go = Rhino.Input.Custom.GetObject()
        go.SetCommandPrompt("OpenNest: Select objects for nesting")
        go.GeometryFilter = geometryFilter
        go.GroupSelect = False
        go.SubObjectSelect = False
        go.EnableClearObjectsOnEntry(True)
        go.EnableUnselectObjectsOnExit(True)
        go.DeselectAllBeforePostSelect = False
        
        bHavePreselectedObjects = False
        
        while True:
            if scriptcontext.escape_test(False): return
            
            res = go.GetMultiple(1, 0)
            if res == Rhino.Input.GetResult.Option:
                go.EnablePreSelect(False, True)
                continue
            elif res != Rhino.Input.GetResult.Object:
                return 
    
            if go.ObjectsWerePreselected:
                bHavePreselectedObjects = True
                go.EnablePreSelect(False, True)
                continue
    
            break
    
        if (bHavePreselectedObjects):
            for i in xrange(go.ObjectCount):
                rhinoObject = go.Object(i).Object()
    
                if not rhinoObject is None:
                    rhinoObject.Select(False)
                    
            scriptcontext.doc.Views.Redraw()
    
        guids = List[System.Guid]()
        for i in xrange(go.ObjectCount):
            guids.Add(go.Object(i).ObjectId)
    
        # Tuple<Brep[], Dictionary<int, List<Guid>>> 
        data = OpenNestLib.OpenNestUtil.SortGuidsByPlanarCurves(guids, self.cp)
        #print type(data)
        
        print "OpenNest: Selected object count = {0}".format(go.ObjectCount)
        self.n = data.Item1.Length
        scriptcontext.doc.Views.Redraw()
        
        return data

def RunCommand():
    
    nester = OpenNestContext()
    
    rc = nester.GetNestingSettings()
    if not rc: return
    
    sheets = nester.GetSheets()
    if not sheets: return
    
    data = nester.GetObjectsToNest()
    if not data: return
    
    outlines = System.Array.CreateInstance(System.Array[Rhino.Geometry.Polyline], nester.n)
    
    for i in xrange(data.Item1.Length):
        outlines[i] = OpenNestLib.OpenNestUtil.BrepLoops(data.Item1[i])
    
    rs.Prompt("Nesting, please wait")
    rc = OpenNestLib.Helpers.Nest(sheets, outlines, data, nester)
    if not rc: print "Nest failed"; return
    print "Nesting done"
    
    #def MyFunc():
    #    OpenNestLib.Helpers.Nest(sheets, outlines, data, nester)
    #t = Task.Run.Overloads[System.Func[Task]](MyFunc)
    #print "OpenNest: Nesting... Keep working while the nesting finishes."
    
    
    
    scriptcontext.doc.Objects.UnselectAll()
    
    breps, dictionary = data
    
    for b in breps: scriptcontext.doc.Objects.AddBrep(b)
    
    for index, guids in zip(dictionary.Keys, dictionary.Values):
        for guid in guids:
            scriptcontext.doc.Objects.Select(guid, True, True)


RunCommand()