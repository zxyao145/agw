"use client";

import * as React from "react";
import { CalendarIcon, Clock2Icon } from "lucide-react";

import { Button } from "./shadcn/button";
import { Calendar } from "./shadcn/calendar";
import { Input } from "./shadcn/input";
import { Label } from "./shadcn/label";
import { Popover, PopoverContent, PopoverTrigger } from "./shadcn/popover";
import {
  formatLocalDateTimeExact,
  formatLocalTimeExact,
  parseApiDateTime,
  replaceLocalDate,
  replaceLocalTime,
} from "../lib/date-time";

type DateTimePickerProps = {
  id: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  clearable?: boolean;
};

function DateTimePicker({
  id,
  value,
  onChange,
  placeholder = "Pick a date",
  clearable = false,
}: DateTimePickerProps) {
  const [dateOpen, setDateOpen] = React.useState(false);
  const [timeZone, setTimeZone] = React.useState<string>();
  const parsedDateTime = parseApiDateTime(value);
  const dateTime = parsedDateTime ?? new Date();

  React.useEffect(() => {
    setTimeZone(Intl.DateTimeFormat().resolvedOptions().timeZone);
  }, []);

  const handleDateChange = (selectedDate?: Date) => {
    if (!selectedDate) {
      return;
    }

    onChange(replaceLocalDate(dateTime, selectedDate).toISOString());
  };

  const handleTimeChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const nextDateTime = replaceLocalTime(dateTime, event.target.value);
    if (nextDateTime) {
      onChange(nextDateTime.toISOString());
    }
  };

  const handleClear = () => {
    onChange("");
    setDateOpen(false);
  };

  return (
    <Popover open={dateOpen} onOpenChange={setDateOpen}>
      <PopoverTrigger asChild>
        <Button
          id={`${id}-date`}
          type="button"
          variant="outline"
          className="w-full min-w-0 justify-start overflow-hidden text-left font-normal"
        >
          <CalendarIcon className="text-muted-foreground" />
          <span className={parsedDateTime ? "truncate" : "truncate text-muted-foreground"}>
            {parsedDateTime ? formatLocalDateTimeExact(dateTime) : placeholder}
          </span>
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-auto p-0" align="start">
        <Calendar
          mode="single"
          selected={parsedDateTime ?? undefined}
          defaultMonth={dateTime}
          onSelect={handleDateChange}
          timeZone={timeZone}
        />
        <div className="space-y-3 border-t p-3">
          <div className="space-y-1.5">
            <Label htmlFor={`${id}-time`}>Time</Label>
            <div className="relative">
              <Input
                id={`${id}-time`}
                type="time"
                step="1"
                value={parsedDateTime ? formatLocalTimeExact(dateTime) : ""}
                onChange={handleTimeChange}
                className="appearance-none pr-9 [&::-webkit-calendar-picker-indicator]:hidden [&::-webkit-calendar-picker-indicator]:appearance-none"
              />
              <Clock2Icon className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground" />
            </div>
          </div>
          {clearable && parsedDateTime ? (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="w-full"
              onClick={handleClear}
            >
              Clear
            </Button>
          ) : null}
        </div>
      </PopoverContent>
    </Popover>
  );
}

export { DateTimePicker };
