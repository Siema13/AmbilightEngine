using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmbilightEngine.Core.Models
{
	/// <summary>
	/// Niektóre wersje firmware WLED zapisują pola liczbowe (np. playlist.transition)
	/// jako string zamiast liczby JSON. Ten konwerter akceptuje oba warianty,
	/// żeby deserializacja /presets.json nie wywalała się na całej odpowiedzi
	/// z powodu jednego niespójnie stypowanego pola.
	/// </summary>
	public sealed class FlexibleNullableIntConverter : JsonConverter<int?>
	{
		public override int? Read(
			ref Utf8JsonReader reader,
			Type typeToConvert,
			JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Null)
			{
				return null;
			}

			if (reader.TokenType == JsonTokenType.Number)
			{
				return reader.GetInt32();
			}

			if (reader.TokenType == JsonTokenType.String)
			{
				string? text = reader.GetString();

				return int.TryParse(text, out int parsed) ? parsed : null;
			}

			return null;
		}

		public override void Write(
			Utf8JsonWriter writer,
			int? value,
			JsonSerializerOptions options)
		{
			if (value.HasValue)
			{
				writer.WriteNumberValue(value.Value);
			}
			else
			{
				writer.WriteNullValue();
			}
		}
	}
}