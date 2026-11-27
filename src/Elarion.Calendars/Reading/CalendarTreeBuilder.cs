using System.Buffers;
using Elarion.ContentLines;

namespace Elarion.Calendars.Reading;

/// <summary>A top-level component of the calendar, with the structural errors found while building it.</summary>
internal sealed class TopLevelComponent(CalendarComponent component, int lineNumber) {
    public CalendarComponent Component { get; } = component;

    public int LineNumber { get; } = lineNumber;

    public List<CalendarError> Errors { get; } = [];

    /// <summary>A per-item limit was exceeded, so the item has no typed value.</summary>
    public bool Limited { get; set; }

    public bool IsItem => Component.Name is "VEVENT" or "VTODO";
}

/// <summary>
/// Builds the component tree from content lines (spec 0004 FR-009–FR-011, FR-041, FR-055): <c>BEGIN</c>/<c>END</c> on a
/// stack, an <c>END</c> of an outer component closes the inner ones, the end of the file closes all. File limits stop
/// the build; per-item limits skip to the item's end and cost only that item.
/// </summary>
internal sealed class CalendarTreeBuilder {
    // RFC 5545 CONTROL: every control character but tab.
    private static readonly SearchValues<char> Controls = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u000A\u000B\u000C\u000D\u000E\u000F"
        + "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F\u007F");

    private readonly CalendarReadOptions _options;
    private readonly ContentTextDecoder _decoder;
    private readonly ContentLineRules _rules;
    private readonly List<CalendarComponent> _stack = [];
    private TopLevelComponent? _current;
    private int _components;
    private int _items;
    private bool _rootLimitReported;

    // While an item is skipped past a limit: the depth of open components inside it still to be closed.
    private int _skipDepth;

    public CalendarTreeBuilder(CalendarReadOptions options, ContentTextDecoder decoder) {
        _options = options;
        _decoder = decoder;
        _rules = ContentLineRules.ICalendar(options.MaxLineBytes);
    }

    public CalendarComponent? Root { get; private set; }

    public List<TopLevelComponent> TopLevel { get; } = [];

    public List<CalendarError> FileErrors { get; } = [];

    public void Build(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
        using var reader = new ContentLineReader(data, _rules);
        if (!reader.Read() || !IsBeginCalendar(reader.Current)) {
            FileErrors.Add(new CalendarError(CalendarErrorCodes.Unreadable, "The file is not an iCalendar file: it holds no VCALENDAR."));
            return;
        }

        Root = new CalendarComponent("VCALENDAR") { LineNumber = reader.LineNumber };
        _stack.Add(Root);
        _components = 1;
        var lines = 1;
        while (reader.Read()) {
            if ((++lines & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (lines > _options.MaxLines) {
                StopAt(new CalendarError(CalendarErrorCodes.TooManyLines, $"The file has more than {_options.MaxLines} lines; reading stopped.") {
                    LineNumber = reader.LineNumber,
                    Limit = _options.MaxLines,
                });
                return;
            }

            if (!Line(reader)) return;
        }

        if (_stack.Count > 0) {
            if (_skipDepth == 0) {
                AddError(new CalendarError(CalendarErrorCodes.UnexpectedEnd,
                    $"The file ends inside {_stack[^1].Name}; the open components were closed.") { Component = _stack[^1].Name });
            }

            _stack.Clear();
        }
    }

    // Handles one logical line; false when a file limit stops the read.
    private bool Line(ContentLineReader reader) {
        if (reader.IsTooLong) {
            if (_skipDepth > 0) return true;
            AddError(new CalendarError(CalendarErrorCodes.LineTooLong,
                $"A line is longer than {_options.MaxLineBytes} bytes; it was skipped.") {
                Property = ContentLineParser.PeekName(reader.Current, false),
                LineNumber = reader.LineNumber,
                Limit = _options.MaxLineBytes,
            });
            return true;
        }

        if (!ContentLineParser.TryParse(reader.Current, _rules, _decoder, out var line)) {
            if (_skipDepth > 0) return true;
            var text = _decoder.Decode(reader.Current[..Math.Min(reader.Current.Length, 200)]);
            AddError(new CalendarError(CalendarErrorCodes.MalformedLine, "The line is not a content line; it was skipped.") {
                LineNumber = reader.LineNumber,
                Value = text,
            });
            return true;
        }

        if (line.Name == "BEGIN") return Begin(_decoder.Decode(line.Value).Trim().ToUpperInvariant(), reader.LineNumber);
        if (line.Name == "END") {
            End(_decoder.Decode(line.Value).Trim().ToUpperInvariant(), reader.LineNumber);
            return true;
        }

        if (_skipDepth > 0 || _stack.Count == 0) return true;
        var parent = _stack[^1];
        if (parent.Properties.Count >= _options.MaxPropertiesPerComponent) {
            if (_current is not null && _stack.Count > 1) {
                LimitItem(new CalendarError(CalendarErrorCodes.TooManyProperties,
                    $"A component has more than {_options.MaxPropertiesPerComponent} properties; the item was not read further.") {
                    LineNumber = reader.LineNumber,
                    Limit = _options.MaxPropertiesPerComponent,
                }, _stack.Count - 1);
            }
            else if (!_rootLimitReported) {
                _rootLimitReported = true;
                FileErrors.Add(new CalendarError(CalendarErrorCodes.TooManyProperties,
                    $"The calendar has more than {_options.MaxPropertiesPerComponent} properties; the rest were skipped.") {
                    LineNumber = reader.LineNumber,
                    Limit = _options.MaxPropertiesPerComponent,
                });
            }

            return true;
        }

        var value = _decoder.Decode(line.Value);
        if (_options.Strict && value.AsSpan().ContainsAny(Controls)) {
            AddError(new CalendarError(CalendarErrorCodes.ControlCharacter, "The value holds a control character; the line was skipped.") {
                Property = line.Name,
                LineNumber = reader.LineNumber,
            });
            return true;
        }

        var property = new CalendarProperty(line.Name, value,
            line.Parameters.Select(static p => new CalendarParameter(p.Name, p.Values))) { LineNumber = reader.LineNumber };
        parent.Properties.Add(property);
        return true;
    }

    private bool Begin(string name, int lineNumber) {
        if (name.Length == 0) name = "X-UNNAMED";
        if (_skipDepth > 0) {
            _skipDepth++;
            return true;
        }

        if (_stack.Count == 0) {
            // Concatenated calendars are read as one; anything else after the end is ignored.
            if (name == "VCALENDAR" && Root is not null) _stack.Add(Root);
            return true;
        }

        if (++_components > _options.MaxComponents) {
            StopAt(new CalendarError(CalendarErrorCodes.TooManyComponents,
                $"The file has more than {_options.MaxComponents} components; reading stopped.") {
                LineNumber = lineNumber,
                Limit = _options.MaxComponents,
            });
            return false;
        }

        if (_stack.Count == 1 && name is "VEVENT" or "VTODO" && ++_items > _options.MaxItems) {
            StopAt(new CalendarError(CalendarErrorCodes.TooManyItems, $"The file has more than {_options.MaxItems} events and to-dos; reading stopped.") {
                LineNumber = lineNumber,
                Limit = _options.MaxItems,
            });
            return false;
        }

        if (_stack.Count + 1 > _options.MaxDepth) {
            LimitItem(new CalendarError(CalendarErrorCodes.NestingTooDeep,
                $"Components are nested deeper than {_options.MaxDepth} levels; the item was not read further.") {
                LineNumber = lineNumber,
                Limit = _options.MaxDepth,
            }, _stack.Count);
            return true;
        }

        var component = new CalendarComponent(name) { LineNumber = lineNumber };
        _stack[^1].Components.Add(component);
        _stack.Add(component);
        if (_stack.Count == 2) {
            _current = new TopLevelComponent(component, lineNumber);
            TopLevel.Add(_current);
        }

        return true;
    }

    private void End(string name, int lineNumber) {
        if (_skipDepth > 0) {
            if (--_skipDepth == 0) {
                // The skipped item is closed: back at the calendar level.
                _stack.RemoveRange(1, _stack.Count - 1);
                _current = null;
            }

            return;
        }

        var index = _stack.FindLastIndex(component => component.Name == name);
        if (index < 0) {
            if (_stack.Count == 0) return;
            AddError(new CalendarError(CalendarErrorCodes.MalformedLine, $"END:{name} closes no open component; it was skipped.") {
                LineNumber = lineNumber,
                Value = "END:" + name,
            });
            return;
        }

        if (index < _stack.Count - 1) {
            AddError(new CalendarError(CalendarErrorCodes.UnexpectedEnd,
                $"END:{name} closes {_stack[^1].Name} before its own END.") {
                LineNumber = lineNumber,
                Component = _stack[^1].Name,
            });
        }

        _stack.RemoveRange(index, _stack.Count - index);
        if (_stack.Count <= 1) _current = null;
    }

    // A file limit: the read stops with one error and keeps the items before the one being read.
    private void StopAt(CalendarError error) {
        FileErrors.Add(error);
        if (_current is not null && _stack.Count > 1) {
            Root!.Components.Remove(_current.Component);
            TopLevel.Remove(_current);
        }

        _current = null;
        _skipDepth = 0;
        _stack.Clear();
    }

    // A per-item limit: the item keeps what was read and has no typed value; the rest of it is skipped.
    private void LimitItem(CalendarError error, int openInsideItem) {
        if (_current is null) {
            FileErrors.Add(error);
            return;
        }

        _current.Errors.Add(error);
        _current.Limited = true;
        _skipDepth = openInsideItem;
    }

    private void AddError(CalendarError error) {
        if (_current is not null && _stack.Count > 1) _current.Errors.Add(error);
        else FileErrors.Add(error);
    }

    private bool IsBeginCalendar(ReadOnlySpan<byte> line) =>
        ContentLineParser.TryParse(line, _rules, _decoder, out var parsed)
        && parsed.Name == "BEGIN"
        && _decoder.Decode(parsed.Value).Trim().Equals("VCALENDAR", StringComparison.OrdinalIgnoreCase);
}
