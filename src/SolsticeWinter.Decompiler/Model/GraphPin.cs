namespace SolsticeWinter.Decompiler.Model;

/// <summary>
/// 一个引脚。移植自 <c>UEBlueprintGraphViewer.Nodes.GraphPin</c>（MIT）。
///
/// <para>
/// **连接只有一份真相**：<see cref="LinkedTo"/>（双向对称——输出脚的列表里是消费者，
/// 输入脚的列表里是生产者）。<see cref="Source"/> 是**派生属性**，不再单独存储。
/// </para>
///
/// <para>
/// 为什么这么改：之前 `Source` 和 `LinkedTo` 各存一份，靠调用方记得两边都写。
/// 结果「取上游」的代码到处要写 `Source ?? LinkedTo.FirstOrDefault()` 的兜底，
/// 宏折叠连着三轮踩同一个坑（索引脚、Condition 脚、exec 入口脚）。
/// 现在物理上不可能不同步。
/// </para>
/// </summary>
public sealed class GraphPin
{
    public const float PinRowHeight = 22;
    public const float HeaderHeight = 28;
    public const float PinTopPadding = 8;

    public GraphPin(string name, PinDirection direction, GraphPinType type)
    {
        Name = name;
        FriendlyName = name;
        Direction = direction;
        Type = type;
    }

    public GraphPin(string name, string friendlyName, PinDirection direction, GraphPinType type)
        : this(name, direction, type)
    {
        FriendlyName = friendlyName;
    }

    /// <summary>内部名（对应 UE 的参数名）。</summary>
    public string Name { get; private set; }

    /// <summary>显示名（如 Branch 的 then/else 显示成 True/False）。</summary>
    public string FriendlyName { get; set; }

    public PinDirection Direction { get; }

    public GraphPinType Type { get; private set; }

    /// <summary>
    /// 重新定型。只给「先读后写」的编译器临时变量用：首次读到时类型未知，
    /// 等到看见赋值才知道真实类型（否则这些脚永远是 Unknown，实测占全部引脚的 42%）。
    /// <summary>
    /// 这个类型是**推断出来的**，不是权威来源。编译器临时变量、以及沿数据边传播得到的类型
    /// 都标成推断——遇到来自签名的权威类型时应当让位。
    ///
    /// <para>
    /// 不加这个区分的话，「临时变量的 Int（猜的）」会顶住「参数要求 Object（签名说的）」，
    /// 于是校验器把数据噪声报成「连错线」（实测 2 处）。
    /// </para>
    /// </summary>
    public bool IsTypeInferred { get; private set; }

    /// <summary>
    /// 重新定型。<paramref name="inferred"/> 为 true 表示这是推断值，可被权威类型覆盖。
    /// </summary>
    public void Retype(GraphPinType type, bool inferred = false)
    {
        Type = type;
        IsTypeInferred = inferred;
    }

    /// <summary>未连线时显示在引脚上的字面量/变量名。</summary>
    public string Value { get; set; } = string.Empty;

    public bool IsInput => Direction == PinDirection.Input;
    public bool IsOutput => Direction == PinDirection.Output;

    public bool IsConnected => LinkedTo.Count > 0;

    /// <summary>连接关系。输出脚里放消费者，输入脚里放生产者；两边始终对称。</summary>
    public List<GraphPin> LinkedTo { get; } = [];

    /// <summary>
    /// 喂这个脚的上游输出脚。**派生**自 <see cref="LinkedTo"/>，不能单独赋值。
    /// </summary>
    public GraphPin? Source => IsInput ? LinkedTo.FirstOrDefault(p => p.IsOutput) : null;

    /// <summary>这个脚喂出去的下游输入脚（输出脚才有）。</summary>
    public IEnumerable<GraphPin> Consumers => IsOutput ? LinkedTo.Where(p => p.IsInput) : [];

    public bool IsNameHidden { get; set; }
    public bool IsHidden { get; private set; }

    /// <summary>
    /// 强制隐藏，**不检查是否已连线**。用于「这个脚不该出现」的场景
    /// （例如函数入口上的编译器临时变量脚）：隐藏后 UI 不建 ConnectorVm，对应连线也不画。
    /// </summary>
    public void HideForce() => IsHidden = true;

    public BPNode? ParentNode { get; private set; }

    public float AnchorX { get; private set; }
    public float AnchorY { get; private set; }

    public void SetName(string name)
    {
        Name = name;
        FriendlyName = name;
    }

    public void Hide()
    {
        if (LinkedTo.Count > 0) throw new InvalidOperationException("已连线的引脚不能隐藏");
        IsHidden = true;
    }

    public void BindToNode(BPNode node)
    {
        ParentNode = node;
        UpdateAnchor();
    }

    /// <summary>
    /// 连一条边。**唯一**的连线入口——同时维护两端的 <see cref="LinkedTo"/>，重复调用无副作用。
    /// 方向必须是一出一入，否则抛异常（早暴露胜过画出反向边）。
    /// </summary>
    public static void Connect(GraphPin output, GraphPin input)
    {
        if (!output.IsOutput || !input.IsInput)
            throw new InvalidOperationException($"连线方向错误：{output.Name}({output.Direction}) → {input.Name}({input.Direction})");

        if (!output.LinkedTo.Contains(input)) output.LinkedTo.Add(input);
        if (!input.LinkedTo.Contains(output)) input.LinkedTo.Add(output);
    }

    /// <summary>断开一条边（两端一起断）。</summary>
    public static void Disconnect(GraphPin a, GraphPin b)
    {
        a.LinkedTo.Remove(b);
        b.LinkedTo.Remove(a);
    }

    /// <summary>把这个脚的所有连接改接到另一个输出脚上（重定向用）。</summary>
    public void RedirectConsumersTo(GraphPin newProducer)
    {
        if (!IsOutput || !newProducer.IsOutput) return;

        foreach (var consumer in LinkedTo.Where(p => p.IsInput).ToList())
        {
            Disconnect(this, consumer);
            Connect(newProducer, consumer);
        }
    }

    /// <summary>按「头高 + 序号 × 行高」算锚点；隐藏的引脚不占行。</summary>
    public void UpdateAnchor()
    {
        if (ParentNode is null) return;

        var siblings = IsOutput ? ParentNode.Output : ParentNode.Input;
        var index = 0;
        foreach (var pin in siblings)
        {
            if (pin.IsHidden) continue;
            if (ReferenceEquals(pin, this)) break;
            index++;
        }

        AnchorX = IsOutput ? ParentNode.Right : ParentNode.X;
        AnchorY = ParentNode.Y + PinTopPadding
                  + (ParentNode.HeaderHidden ? 0 : HeaderHeight)
                  + index * PinRowHeight;
    }
}

