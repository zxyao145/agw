const validator = () => null;

validator.isRequired = validator;

const PropTypes = {
  any: validator,
  array: validator,
  arrayOf: () => validator,
  bool: validator,
  checkPropTypes: () => undefined,
  element: validator,
  exact: () => validator,
  func: validator,
  instanceOf: () => validator,
  node: validator,
  number: validator,
  object: validator,
  objectOf: () => validator,
  oneOf: () => validator,
  oneOfType: () => validator,
  resetWarningCache: () => undefined,
  shape: () => validator,
  string: validator,
  symbol: validator,
};

module.exports = PropTypes;
module.exports.default = PropTypes;
