const fs = require('fs');
const b = fs.readFileSync('manya.db');
console.log('users=', b.indexOf(Buffer.from('users', 'utf8')));
console.log('create users=', b.indexOf(Buffer.from('CREATE TABLE users', 'utf8')));
console.log('public_profiles=', b.indexOf(Buffer.from('public_profiles', 'utf8')));
