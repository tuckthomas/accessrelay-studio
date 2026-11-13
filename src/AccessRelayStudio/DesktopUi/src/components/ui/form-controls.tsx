import { Children, Fragment, isValidElement, useEffect, useRef, useState } from 'react';
import type { ButtonHTMLAttributes, ComponentProps, ReactNode } from 'react';
import * as Primitive from '@radix-ui/react-select';
import { Check, ChevronDown, ChevronUp } from 'lucide-react';

export function Input({ className = '', ...props }: ComponentProps<'input'>) {
  return <input data-slot="input" className={'ui-input ' + className} {...props} />;
}
export function Slider(props: ComponentProps<'input'>) { return <Input {...props} data-slot="slider" type="range" />; }
export function Checkbox(props: ComponentProps<'input'>) { return <Input {...props} data-slot="checkbox" type="checkbox" />; }
export function Textarea({ className = '', ...props }: ComponentProps<'textarea'>) {
  return <textarea data-slot="textarea" className={'ui-textarea ' + className} {...props} />;
}
type OptionProps = { value?: string | number; children?: ReactNode; disabled?: boolean };
export function SelectOption(_props: OptionProps) { return null; }
function optionsOf(children: ReactNode): OptionProps[] {
  const options: OptionProps[] = [];
  Children.forEach(children, child => {
    if (!isValidElement(child)) return;
    if (child.type === Fragment) options.push(...optionsOf((child.props as { children: ReactNode }).children));
    else if (child.type === SelectOption) options.push(child.props as OptionProps);
    else if (child.type === 'optgroup') {
      const group = child.props as { children: ReactNode; disabled?: boolean };
      options.push(...optionsOf(group.children).map(option => ({ ...option, disabled: group.disabled || option.disabled })));
    }
  });
  return options;
}
const textOf = (children: ReactNode): string => Children.toArray(children).map(child => isValidElement(child)
  ? textOf((child.props as { children: ReactNode }).children) : String(child ?? '')).join('');
type SelectProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'value' | 'defaultValue' | 'onChange'> & {
  value?: string | number; defaultValue?: string | number; required?: boolean; onValueChange?: (value: string) => void;
};

// One app-wide Select: keyboard/typeahead, focus return, disabled options and a
// rounded, collision-aware popup, following the CRM's Radix component pattern.
export function Select({ children, value, defaultValue, onValueChange, disabled, name, required, className = '', title, ...props }: SelectProps) {
  const trigger = useRef<HTMLButtonElement>(null);
  const [container, setContainer] = useState<HTMLElement | undefined>();
  const [boundary, setBoundary] = useState<Element | null>(null);
  const [uncontrolled, setUncontrolled] = useState(defaultValue === undefined ? '' : String(defaultValue));
  const options = optionsOf(children), selected = value === undefined ? uncontrolled : String(value);
  const selectedOption = options.find(option => String(option.value ?? textOf(option.children)) === selected);
  const scanState = useRef({ options, selected, disabled, onValueChange });
  scanState.current = { options, selected, disabled, onValueChange };
  useEffect(() => {
    const element = trigger.current;
    const adjust = (event: Event) => {
      const direction = (event as CustomEvent<number>).detail, current = scanState.current;
      if (current.disabled || (direction !== 1 && direction !== -1)) return;
      const choices = current.options.filter(option => !option.disabled).map(option => String(option.value ?? textOf(option.children)));
      if (!choices.length) return;
      const index = choices.indexOf(current.selected);
      const next = choices[index < 0 ? direction > 0 ? 0 : choices.length - 1 : (index + direction + choices.length) % choices.length];
      setUncontrolled(next); current.onValueChange?.(next);
    };
    element?.addEventListener('accessrelay-select-adjust', adjust);
    return () => element?.removeEventListener('accessrelay-select-adjust', adjust);
  }, []);
  const encode = (item: string) => 'value:' + item;
  return <Primitive.Root value={selectedOption ? encode(selected) : ''} disabled={disabled || !options.length} name={name} required={required}
    onValueChange={next => { const raw = next.slice(6); setUncontrolled(raw); onValueChange?.(raw); }}
    onOpenChange={open => { if (open) { setContainer(trigger.current?.closest('dialog') ?? undefined); setBoundary(trigger.current?.closest('.modal, .app-main') ?? null); } }}>
    <Primitive.Trigger {...props} ref={trigger} type="button" value={selected} data-scan-choice="" data-slot="select-trigger" className={'ui-select-trigger ' + className}
      title={title ?? textOf(selectedOption?.children)}>
      <Primitive.Value placeholder={textOf(options[0]?.children) || 'Choose an option'} />
      <Primitive.Icon asChild><ChevronDown size={17} aria-hidden="true" /></Primitive.Icon>
    </Primitive.Trigger>
    <Primitive.Portal container={container}><Primitive.Content data-slot="select-content" className="ui-select-content" position="popper"
      align="start" sideOffset={6} collisionPadding={10} collisionBoundary={boundary} avoidCollisions hideWhenDetached>
      <Primitive.ScrollUpButton className="ui-select-scroll"><ChevronUp size={17} aria-hidden="true" /></Primitive.ScrollUpButton>
      <Primitive.Viewport className="ui-select-viewport">{options.map((option, index) => <Primitive.Item key={String(option.value ?? index)}
        value={encode(String(option.value ?? textOf(option.children)))} data-value={String(option.value ?? textOf(option.children))} disabled={option.disabled} textValue={textOf(option.children)} className="ui-select-item">
        <Primitive.ItemText>{option.children}</Primitive.ItemText><Primitive.ItemIndicator><Check size={16} aria-hidden="true" /></Primitive.ItemIndicator>
      </Primitive.Item>)}</Primitive.Viewport>
      <Primitive.ScrollDownButton className="ui-select-scroll"><ChevronDown size={17} aria-hidden="true" /></Primitive.ScrollDownButton>
    </Primitive.Content></Primitive.Portal>
  </Primitive.Root>;
}
