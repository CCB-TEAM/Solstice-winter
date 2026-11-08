using System.Windows;
using SolsticeWinter.Decompiler.Model;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglNode = Microsoft.Msagl.Core.Layout.Node;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 用 Microsoft.Msagl 做分层布局（与参考项目 UEBGV 同一套算法）。
///
/// <para>
/// 输入是移植来的 <see cref="BPNode"/> 图：exec 边参与布局（决定层次），
/// 数据边只用来把纯节点拉到消费者附近（权重更低，不强制成层）。
/// </para>
///
/// <para>
/// 坐标约定：Msagl 的 Y 轴向上，WPF 向下，所以输出时对 Y 取反；
/// 最后整体平移，让最小角落在 (60, 60)，避免节点贴在画布边缘。
/// </para>
/// </summary>
public static class MsaglLayout
{
    public sealed record Result(bool Applied, string? FallbackReason, int Nodes, int Edges);

    public static Result Apply(IReadOnlyList<BPNode> nodes, IReadOnlyDictionary<BPNode, Point> sizes,
        Action<BPNode, Point> assign)
    {
        if (nodes.Count == 0) return new Result(false, "没有节点", 0, 0);

        try
        {
            var graph = new GeometryGraph();
            var byNode = new Dictionary<BPNode, MsaglNode>(nodes.Count);

            foreach (var node in nodes)
            {
                var size = sizes.TryGetValue(node, out var s) ? s : new Point(240, 120);
                var msaglNode = new MsaglNode(CurveFactory.CreateRectangle(
                    Math.Max(120, size.X), Math.Max(60, size.Y), new Microsoft.Msagl.Core.Geometry.Point(0, 0)))
                {
                    UserData = node,
                };
                graph.Nodes.Add(msaglNode);
                byNode[node] = msaglNode;
            }

            var edgeCount = 0;
            foreach (var node in nodes)
            {
                foreach (var pin in node.Output)
                {
                    var isExec = pin.Type.Category == PinType.Exec;
                    foreach (var linked in pin.LinkedTo)
                    {
                        if (linked.ParentNode is not { } target) continue;
                        if (!byNode.TryGetValue(node, out var from) || !byNode.TryGetValue(target, out var to)) continue;
                        if (ReferenceEquals(from, to)) continue;

                        graph.Edges.Add(new Microsoft.Msagl.Core.Layout.Edge(from, to)
                        {
                            Weight = isExec ? 2 : 1,
                        });
                        edgeCount++;
                    }
                }
            }

            var settings = new SugiyamaLayoutSettings
            {
                LayerSeparation = 68,
                NodeSeparation = 26,
                EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.Spline },

                // Msagl 默认分层是**从上到下**，图会长成又高又窄的一条（实测 1524×3509）。
                // UE 编辑器的图是**从左往右**流动的，把整张布局旋转 90° 才像。
                Transformation = Microsoft.Msagl.Core.Geometry.Curves.PlaneTransformation.Rotation(Math.PI / 2),
            };

            LayoutHelpers.CalculateLayout(graph, settings, null);

            // Msagl 的 Y 向上 → 取反；再整体平移
            var positions = new Dictionary<BPNode, Point>(nodes.Count);
            var minX = double.MaxValue;
            var minY = double.MaxValue;

            foreach (var (node, msaglNode) in byNode)
            {
                var size = sizes.TryGetValue(node, out var s) ? s : new Point(240, 120);
                var x = msaglNode.Center.X - size.X / 2;
                var y = -(msaglNode.Center.Y + size.Y / 2);
                positions[node] = new Point(x, y);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
            }

            foreach (var node in nodes)
            {
                var p = positions[node];
                assign(node, new Point(p.X - minX + 60, p.Y - minY + 60));
            }

            return new Result(true, null, nodes.Count, edgeCount);
        }
        catch (Exception e)
        {
            return new Result(false, $"{e.GetType().Name}: {e.Message}", nodes.Count, 0);
        }
    }
}






