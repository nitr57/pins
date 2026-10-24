### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
EXP0002 | Usage | Error | Handwritten validation conflicts with generated validation
EXP0003 | Usage | Error | Invalid expression validation condition
EXP0004 | Usage | Error | Invalid expression proxy
EXP0005 | Usage | Error | Incompatible Issues property
EXP0006 | Usage | Error | Inherited validation requires explicit composition
EXP0007 | Usage | Error | Handwritten Issues conflicts with generated validation
EXP0008 | Usage | Error | Handwritten member conflicts with generated expression validation helper
EXP0009 | Usage | Error | Unsupported expression declaration
EXP0010 | Usage | Error | Expression validator implementation is missing
EXP0011 | Usage | Error | Invalid expression range
EXP0100 | Usage | Warning | Expression cache bypasses evaluation
EXP0101 | Usage | Warning | Validation hook changes discarded issues
EXP0102 | Usage | Warning | Expression entity does not participate in validation
EXP0103 | Usage | Info | Consider composing generated expression validation
EXP0104 | Usage | Warning | Expression validator reenters its scalar property

### Changed Rules

Rule ID | New Category | New Severity | Old Category | Old Severity | Notes
--------|--------------|--------------|--------------|--------------|-------
EXP0001 | Usage | Error | Usage | Hidden | Missing UsesExpressions is a source-generation error
