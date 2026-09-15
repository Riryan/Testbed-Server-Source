namespace Game.Shared.Interactions
{
    public readonly struct InteractionResult
    {
        public uint Sequence { get; }
        public InteractionCategoryId CategoryId { get; }
        public InteractionActionId ActionId { get; }
        public InteractionResultCode ResultCode { get; }
        public InteractionTargetHandle Target { get; }
        public string Detail { get; }
        public bool Success => ResultCode == InteractionResultCode.Success;

        public InteractionResult(
            uint sequence,
            InteractionActionId actionId,
            InteractionResultCode resultCode,
            InteractionTargetHandle target,
            string detail = "")
        {
            Sequence = sequence;
            CategoryId = InteractionCategoryCatalog.ForTargetAction(target.Kind, actionId);
            ActionId = actionId;
            ResultCode = resultCode;
            Target = target;
            Detail = detail ?? string.Empty;
        }
    }
}
