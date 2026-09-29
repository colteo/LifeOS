namespace LifeOS.App.Services;

public sealed record ApiResult<T>
{
	private ApiResult(T? value, IReadOnlyList<string> errors)
	{
		Value = value;
		Errors = errors;
	}

	public T? Value { get; }

	public IReadOnlyList<string> Errors { get; }

	public bool IsSuccess => Errors.Count == 0;

	public static ApiResult<T> Success(T value) => new(value, []);

	public static ApiResult<T> Failure(IReadOnlyList<string> errors) =>
		new(default, errors.Count > 0 ? errors : ["The request failed."]);

	public static ApiResult<T> Failure(string error) => new(default, [error]);
}
