namespace MCLCS.Core.Auth;

/// <summary>
/// 认证失败异常。
/// <para>
/// <see cref="Exception.Message"/> 一律是**可直接展示给用户**的中文说明：
/// 不再把 <c>KeyNotFoundException</c>（"The given key was not present in the dictionary"）、
/// <c>HttpRequestException</c>（"Response status code does not indicate success: 403"）
/// 这类内部细节原样抛给界面。
/// </para>
/// </summary>
public class AuthException : Exception
{
    public AuthException(string message) : base(message) { }

    public AuthException(string message, Exception? inner) : base(message, inner) { }
}
