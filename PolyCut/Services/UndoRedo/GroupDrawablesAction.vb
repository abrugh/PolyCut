Imports PolyCut.[Shared]
Imports PolyCut.RichCanvas

' User grouping: Group turns the selected top-level items into one NestedDrawableGroup (the same visual group SVG
' import builds for <g>, so it moves/scales/rotates as one item, saves/loads and exports with no extra code).
' Ungroup dissolves selected NestedDrawableGroups back into independent items at the same world placement.
' Both restore the exact previous state on undo.

Friend Module GroupingHelpers

    ''' <summary>Wrapper of a top-level drawable on the canvas.</summary>
    Friend Function WrapperOf(d As IDrawable) As ContentControl
        Return TryCast(d?.DrawableElement?.Parent, ContentControl)
    End Function

    ''' <summary>Puts a drawable on the canvas (a new wrapper is created from its element) and applies a placement.</summary>
    Friend Sub PlaceOnCanvas(manager As IDrawableManager, d As IDrawable, state As TransformState, index As Integer)
        Dim fe = TryCast(d.DrawableElement, FrameworkElement)
        If fe Is Nothing Then Return

        fe.Width = state.Width
        fe.Height = state.Height
        Canvas.SetLeft(fe, state.Translation.X)
        Canvas.SetTop(fe, state.Translation.Y)

        manager.AddDrawableToCollection(d, index)

        Dim w = WrapperOf(d)
        If w Is Nothing Then Return
        state.ApplyToWrapper(w)
        If w.RenderTransform Is Nothing Then w.RenderTransform = New RotateTransform(0)

        ' ApplyToWrapper only writes a mirror when it isn't (1,1); make sure an un-mirror sticks too
        Dim tg = TryCast(fe.RenderTransform, TransformGroup)
        Dim st = If(tg IsNot Nothing, tg.Children.OfType(Of ScaleTransform)().FirstOrDefault(), TryCast(fe.RenderTransform, ScaleTransform))
        If st IsNot Nothing Then
            st.ScaleX = state.Scale.X
            st.ScaleY = state.Scale.Y
        End If

        Dim line = TryCast(fe, Line)
        If line IsNot Nothing Then DrawableWrapperFactory.FitLineToWrapper(line, w, line.StrokeThickness)
    End Sub

    ''' <summary>Takes a top-level drawable off the canvas and frees its element for re-wrapping.</summary>
    Friend Sub TakeOffCanvas(manager As IDrawableManager, d As IDrawable)
        Dim w = WrapperOf(d)
        PolyCanvas.RemoveFromSelection(d)
        d.IsSelected = False
        manager.RemoveDrawableFromCollection(d)
        If w IsNot Nothing Then w.Content = Nothing
    End Sub

    ''' <summary>World placement of a group child, from its full wrapper -> canvas transform (child rotation, group
    ''' scale, rotation and mirror). Exact unless a rotated child sits in a non-uniformly stretched group (skew),
    ''' where it becomes the closest rotated rectangle.</summary>
    Friend Function WorldStateOfGroupChild(childWrapper As ContentControl, canvasRoot As UIElement) As TransformState
        Dim local = TransformState.FromWrapper(childWrapper)
        Dim size = TransformMath.GetWrapperSize(childWrapper)
        Dim m = TransformMath.GetAccumulatedMatrix(childWrapper, canvasRoot)

        Dim sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12)
        If sx < 0.000001 Then sx = 1
        Dim sy = (m.M11 * m.M22 - m.M12 * m.M21) / sx          ' negative = mirrored
        Dim angle = Math.Atan2(m.M12, m.M11) * 180.0 / Math.PI
        If Math.Abs(angle) < 0.0001 Then angle = 0

        Dim w = size.Width * sx
        Dim h = size.Height * Math.Abs(sy)
        Dim center = m.Transform(New Point(size.Width / 2, size.Height / 2))
        Dim mirrorY = If(sy < 0, -1.0, 1.0)

        Return New TransformState(New Point(center.X - w / 2, center.Y - h / 2), angle,
                                  New Point(local.Scale.X, local.Scale.Y * mirrorY), New Point(0.5, 0.5)) With {.Width = w, .Height = h}
    End Function

    Friend Sub SelectOnly(mainVM As MainViewModel, items As IEnumerable(Of IDrawable))
        PolyCanvas.ClearSelection()
        For Each d In items
            PolyCanvas.AddToSelection(d)
            d.IsSelected = True
        Next
        mainVM.NotifyCollectionsChanged()
    End Sub

    Friend Function NextGroupName(mainVM As MainViewModel) As String
        Dim used As New HashSet(Of Integer)
        For Each d In mainVM.DrawableCollection
            Dim n As Integer
            If d?.Name IsNot Nothing AndAlso d.Name.StartsWith("Group ") AndAlso Integer.TryParse(d.Name.Substring(6), n) Then used.Add(n)
        Next
        Dim i = 1
        While used.Contains(i)
            i += 1
        End While
        Return $"Group {i}"
    End Function

End Module


Public Class GroupDrawablesAction : Implements IUndoableAction

    Private ReadOnly _mainVM As MainViewModel
    Private ReadOnly _items As New List(Of (Drawable As IDrawable, Index As Integer, Parent As DrawableGroup, ParentIndex As Integer, World As TransformState))
    Private _group As NestedDrawableGroup
    Private _groupParent As DrawableGroup
    Private _groupIndex As Integer
    Private _bounds As Rect

    Public Sub New(mainVM As MainViewModel, selection As IEnumerable(Of IDrawable))
        _mainVM = mainVM
        For Each d In selection.Where(Function(x) x IsNot Nothing AndAlso Not TypeOf x Is DrawableGroup AndAlso Not ProjectGraph.HasNestedAncestor(x)).Distinct()
            Dim w = WrapperOf(d)
            Dim idx = mainVM.DrawableCollection.IndexOf(d)
            If w Is Nothing OrElse idx < 0 Then Continue For
            Dim parent = mainVM.GetParentGroup(d)
            _items.Add((d, idx, parent, If(parent IsNot Nothing, parent.GroupChildren.IndexOf(d), -1), TransformState.FromWrapper(w)))
        Next
        _items.Sort(Function(a, b) a.Index.CompareTo(b.Index))
    End Sub

    Public ReadOnly Property Description As String Implements IUndoableAction.Description
        Get
            Return $"Group: {_group?.Name}"
        End Get
    End Property

    Public Function Execute() As Boolean Implements IUndoableAction.Execute
        If _items.Count < 2 Then Return False

        ' Group frame = union of the items' (unrotated) boxes - the same frame import and project load use
        _bounds = Rect.Empty
        For Each it In _items
            _bounds.Union(New Rect(it.World.Translation.X, it.World.Translation.Y, it.World.Width, it.World.Height))
        Next

        _group = New NestedDrawableGroup(NextGroupName(_mainVM))
        _groupParent = If(_items.Last().Parent, _mainVM.DrawingGroup)
        _groupIndex = _items.Last().Index - (_items.Count - 1)   ' takes the topmost item's place in the z-order

        DoGroup()
        Return True
    End Function

    Private Sub DoGroup()
        For Each it In Enumerable.Reverse(_items)
            TakeOffCanvas(_mainVM, it.Drawable)
        Next

        Dim locals = _items.Select(Function(it)
                                       Dim st = it.World.Clone()
                                       st.Translation = New Point(st.Translation.X - _bounds.Left, st.Translation.Y - _bounds.Top)
                                       Return (it.Drawable, st)
                                   End Function).ToList()
        _group.AttachChildren(locals, _bounds.Size)

        Dim vb = TryCast(_group.DrawableElement, FrameworkElement)
        vb.Width = _bounds.Width
        vb.Height = _bounds.Height
        Canvas.SetLeft(vb, _bounds.Left)
        Canvas.SetTop(vb, _bounds.Top)

        If Not _mainVM.ImportedGroups.Contains(_groupParent) AndAlso _groupParent IsNot _mainVM.DrawingGroup Then _mainVM.ImportedGroups.Add(_groupParent)
        _groupParent.AddChild(_group)
        _mainVM.AddDrawableToCollection(_group, _groupIndex)

        SelectOnly(_mainVM, {_group})
    End Sub

    Public Sub Undo() Implements IUndoableAction.Undo
        If _group Is Nothing Then Return

        TakeOffCanvas(_mainVM, _group)
        _groupParent.RemoveChild(_group)
        _group.DetachChildren()

        For Each it In _items      ' ascending original index restores the exact z-order
            it.Parent?.AddChild(it.Drawable, it.ParentIndex)
            PlaceOnCanvas(_mainVM, it.Drawable, it.World, it.Index)
        Next

        SelectOnly(_mainVM, _items.Select(Function(it) it.Drawable))
    End Sub

    Public Sub Redo() Implements IUndoableAction.Redo
        If _group Is Nothing Then Return
        DoGroup()
    End Sub

End Class


Public Class UngroupDrawablesAction : Implements IUndoableAction

    Private NotInheritable Class GroupRecord
        Public Group As NestedDrawableGroup
        Public Parent As DrawableGroup
        Public ParentIndex As Integer
        Public Index As Integer
        Public Shift As Integer          ' children of earlier dissolved groups sitting below this one
        Public GroupState As TransformState
        Public NativeSize As Size
        Public Locals As List(Of (Drawable As IDrawable, LocalState As TransformState))
        Public Worlds As List(Of TransformState)
    End Class

    Private ReadOnly _mainVM As MainViewModel
    Private ReadOnly _records As New List(Of GroupRecord)

    Public Sub New(mainVM As MainViewModel, selection As IEnumerable(Of IDrawable))
        _mainVM = mainVM
        For Each g In selection.OfType(Of NestedDrawableGroup)().Where(Function(x) Not ProjectGraph.HasNestedAncestor(x)).Distinct()
            Dim idx = mainVM.DrawableCollection.IndexOf(g)
            If idx < 0 OrElse WrapperOf(g) Is Nothing Then Continue For
            Dim parent = TryCast(g.ParentGroup, DrawableGroup)
            _records.Add(New GroupRecord With {.Group = g, .Index = idx, .Parent = parent,
                                               .ParentIndex = If(parent IsNot Nothing, parent.GroupChildren.IndexOf(g), -1)})
        Next
        _records.Sort(Function(a, b) a.Index.CompareTo(b.Index))
    End Sub

    Public ReadOnly Property Description As String Implements IUndoableAction.Description
        Get
            Return "Ungroup"
        End Get
    End Property

    Public Function Execute() As Boolean Implements IUndoableAction.Execute
        If _records.Count = 0 Then Return False

        For Each r In _records
            Dim gw = WrapperOf(r.Group)
            r.GroupState = TransformState.FromWrapper(gw)
            Dim native = r.Group.GetNativeSize()
            r.NativeSize = New Size(native.Item1, native.Item2)
            ' World placement must be read while the children are still inside the group
            Dim root = TryCast(gw.Parent, UIElement)
            r.Worlds = r.Group.GroupChildren.Select(Function(c) WorldStateOfGroupChild(WrapperOf(c), root)).ToList()
        Next

        DoUngroup()
        Return True
    End Function

    Private Sub DoUngroup()
        Dim released As New List(Of IDrawable)
        Dim shift = 0      ' each dissolved group above adds (children - 1) items below the next one

        For Each r In _records
            r.Shift = shift
            TakeOffCanvas(_mainVM, r.Group)
            r.Parent?.RemoveChild(r.Group)
            r.Locals = r.Group.DetachChildren()

            Dim parent = If(r.Parent, _mainVM.DrawingGroup)
            For i = 0 To r.Locals.Count - 1
                Dim d = r.Locals(i).Drawable
                parent.AddChild(d, If(r.ParentIndex >= 0, r.ParentIndex + i, -1))
                PlaceOnCanvas(_mainVM, d, r.Worlds(i), r.Index + shift + i)
                released.Add(d)
            Next
            shift += r.Locals.Count - 1
        Next

        SelectOnly(_mainVM, released)
    End Sub

    Public Sub Undo() Implements IUndoableAction.Undo
        For Each r In Enumerable.Reverse(_records)
            Dim parent = If(r.Parent, _mainVM.DrawingGroup)
            For Each c In r.Locals
                TakeOffCanvas(_mainVM, c.Drawable)
                parent.RemoveChild(c.Drawable)
            Next

            r.Group.AttachChildren(r.Locals, r.NativeSize)

            Dim vb = TryCast(r.Group.DrawableElement, FrameworkElement)
            vb.Width = r.GroupState.Width
            vb.Height = r.GroupState.Height
            Canvas.SetLeft(vb, r.GroupState.Translation.X)
            Canvas.SetTop(vb, r.GroupState.Translation.Y)

            r.Parent?.AddChild(r.Group, r.ParentIndex)
            _mainVM.AddDrawableToCollection(r.Group, r.Index + r.Shift)
            Dim gw = WrapperOf(r.Group)
            If gw IsNot Nothing Then
                r.GroupState.ApplyToWrapper(gw)
                If gw.RenderTransform Is Nothing Then gw.RenderTransform = New RotateTransform(0)
            End If
        Next

        SelectOnly(_mainVM, _records.Select(Function(r) CType(r.Group, IDrawable)))
    End Sub

    Public Sub Redo() Implements IUndoableAction.Redo
        DoUngroup()
    End Sub

End Class
