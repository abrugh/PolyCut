Imports System.Linq

Imports CommunityToolkit.Mvvm.Input

Imports PolyCut.RichCanvas
Imports PolyCut.Shared

' Align / distribute the selected canvas items. Works on rotation-aware world bounds, moves only
' (never resizes), and records the whole operation as one undo step via the same
' TransformCompletedMessage path as MirrorSelection.
Partial Public Class SVGPageViewModel

    Public Enum AlignEdge
        Left
        CenterX     ' same left-right centre: items line up in a column ("Center Vertically" in the UI)
        Right
        Top
        CenterY     ' same up-down centre: items line up in a row ("Center Horizontally" in the UI)
        Bottom
    End Enum

    Private _DistributeGap As Double = 5
    ''' <summary>Gap (mm) used by "distribute with gap".</summary>
    Public Property DistributeGap As Double
        Get
            Return _DistributeGap
        End Get
        Set(value As Double)
            If Double.IsNaN(value) OrElse value < 0 Then value = 0
            SetProperty(_DistributeGap, value)
        End Set
    End Property

    Public Property AlignCommand As ICommand = New RelayCommand(Of AlignEdge)(Sub(edge) AlignSelection(edge))
    Public Property DistributeHorizontallyCommand As ICommand = New RelayCommand(Sub() DistributeSelection(horizontal:=True, fixedGap:=Nothing))
    Public Property DistributeVerticallyCommand As ICommand = New RelayCommand(Sub() DistributeSelection(horizontal:=False, fixedGap:=Nothing))
    Public Property DistributeGapHorizontallyCommand As ICommand = New RelayCommand(Sub() DistributeSelection(horizontal:=True, fixedGap:=DistributeGap))
    Public Property DistributeGapVerticallyCommand As ICommand = New RelayCommand(Sub() DistributeSelection(horizontal:=False, fixedGap:=DistributeGap))


    ''' <summary>Top-level selected items with their wrappers and world bounds. Children inside a visual
    ''' group are skipped - their wrappers live in the group's local space.</summary>
    Private Function GetAlignableItems() As List(Of (Item As IDrawable, Wrapper As ContentControl, Bounds As Rect))
        Dim result As New List(Of (IDrawable, ContentControl, Rect))
        For Each item In PolyCanvas.SelectedItems.ToList()
            If item?.DrawableElement Is Nothing Then Continue For
            If ProjectGraph.HasNestedAncestor(item) Then Continue For
            Dim wrapper = TryCast(item.DrawableElement.Parent, ContentControl)
            If wrapper Is Nothing Then Continue For
            Dim b = TransformMath.GetWorldBounds(wrapper)
            If b.IsEmpty Then Continue For
            result.Add((item, wrapper, b))
        Next
        Return result
    End Function


    ''' <summary>Align to the selection's bounds; with a single item, align it to the working area
    ''' (the sheet) instead.</summary>
    Private Sub AlignSelection(edge As AlignEdge)
        Dim items = GetAlignableItems()
        If items.Count = 0 Then Return

        Dim reference As Rect
        If items.Count = 1 Then
            reference = MainVM.Printer.WorkingRect
        Else
            reference = items(0).Bounds
            For Each it In items.Skip(1)
                reference.Union(it.Bounds)
            Next
        End If

        MoveItems(items, Function(b)
                             Select Case edge
                                 Case AlignEdge.Left : Return New Vector(reference.Left - b.Left, 0)
                                 Case AlignEdge.Right : Return New Vector(reference.Right - b.Right, 0)
                                 Case AlignEdge.CenterX : Return New Vector((reference.Left + reference.Width / 2) - (b.Left + b.Width / 2), 0)
                                 Case AlignEdge.Top : Return New Vector(0, reference.Top - b.Top)
                                 Case AlignEdge.Bottom : Return New Vector(0, reference.Bottom - b.Bottom)
                                 Case Else : Return New Vector(0, (reference.Top + reference.Height / 2) - (b.Top + b.Height / 2))
                             End Select
                         End Function)
    End Sub


    ''' <summary>
    ''' Space items out along one axis, in their current order.
    ''' fixedGap Nothing: equal gaps, the two outermost items stay put (needs 3+ items).
    ''' fixedGap set: that gap (mm) between neighbours, the first item stays put (needs 2+ items).
    ''' </summary>
    Private Sub DistributeSelection(horizontal As Boolean, fixedGap As Double?)
        Dim items = GetAlignableItems()
        Dim minCount = If(fixedGap.HasValue, 2, 3)
        If items.Count < minCount Then Return

        Dim start = Function(b As Rect) If(horizontal, b.Left, b.Top)
        Dim size = Function(b As Rect) If(horizontal, b.Width, b.Height)

        Dim ordered = items.OrderBy(Function(it) start(it.Bounds)).ToList()

        Dim gap As Double
        If fixedGap.HasValue Then
            gap = fixedGap.Value
        Else
            Dim first = ordered.First().Bounds
            Dim last = ordered.Last().Bounds
            Dim span = (start(last) + size(last)) - start(first)
            gap = (span - ordered.Sum(Function(it) size(it.Bounds))) / (ordered.Count - 1)
        End If

        ' Target start of each item, walking from the first one
        Dim targets As New Dictionary(Of ContentControl, Double)
        Dim cursor = start(ordered(0).Bounds)
        For Each it In ordered
            targets(it.Wrapper) = cursor
            cursor += size(it.Bounds) + gap
        Next

        MoveItems(ordered, Nothing, Function(it)
                                        Dim d = targets(it.Wrapper) - start(it.Bounds)
                                        Return If(horizontal, New Vector(d, 0), New Vector(0, d))
                                    End Function)
    End Sub


    Private Sub MoveItems(items As List(Of (Item As IDrawable, Wrapper As ContentControl, Bounds As Rect)),
                          deltaForBounds As Func(Of Rect, Vector),
                          Optional deltaForItem As Func(Of (Item As IDrawable, Wrapper As ContentControl, Bounds As Rect), Vector) = Nothing)
        Dim snapshots As New List(Of (Target As IDrawable, Before As Object, After As Object))

        For Each it In items
            Dim delta = If(deltaForItem IsNot Nothing, deltaForItem(it), deltaForBounds(it.Bounds))
            If Math.Abs(delta.X) < 0.000001 AndAlso Math.Abs(delta.Y) < 0.000001 Then Continue For

            Dim beforeSnapshot = TransformAction.MakeSnapshotFromWrapper(it.Wrapper)
            TransformAction.ApplyMove(it.Wrapper, delta.X, delta.Y)
            Dim afterSnapshot = TransformAction.MakeSnapshotFromWrapper(it.Wrapper)

            If beforeSnapshot IsNot Nothing AndAlso afterSnapshot IsNot Nothing Then
                snapshots.Add((it.Item, CType(beforeSnapshot, Object), CType(afterSnapshot, Object)))
            End If
        Next

        PublishTransformMessage(snapshots)
    End Sub

End Class
