namespace ckapi.Utils;

/// <summary>
/// 统一对外的错误文案：异常详情只写日志，不随响应发给调用方。
/// </summary>
public static class Api
{
    public const string InternalErrorMessage = "服务器内部错误，详见后端日志";
}
